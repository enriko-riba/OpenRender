using OpenRender;
using OpenRender.Core;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.World.Registry;

namespace SpyroGame.World;

/// <summary>
/// Manages item textures and materials.
/// Loads textures for all ItemIds at startup and reports missing ones.
/// </summary>
public sealed class ItemTextureManager
{
    private readonly Dictionary<ItemId, Material> materials = [];
    private Material defaultMaterial = Material.Default;

    private const string ItemTextureDir = "Resources/voxel/items";
    private const string BlockTextureDir = "Resources/voxel/blocks";

    public void Initialize()
    {
        LoadItemMaterials();
    }

    public bool IsBlockItem(ItemId item) => ItemRegistry.Get(item) is BlockItem;

    private void LoadItemMaterials()
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

    public Material GetMaterial(ItemId item)
    {
        return materials.TryGetValue(item, out var mat) ? mat : defaultMaterial;
    }

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
}
