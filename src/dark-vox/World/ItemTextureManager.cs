using OpenRender;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using DarkVox.Shared.World.Registry;

namespace DarkVox.World;

/// <summary>
/// Manages item textures and materials for rendering items in inventory, hotbar, and world.
/// Uses GameObject.TexturePath when available, falls back to convention-based paths.
/// </summary>
public static class ItemTextureManager
{
    private static readonly Dictionary<GameObjectId, TextureDescriptor> descriptors = [];
    private static readonly Dictionary<(GameObjectId Id, int ShaderId), Material> materials = [];
    private static readonly Material defaultMaterial = Material.Default;
    private static Shader? spriteShader;
    private static Shader? worldItemShader;
    private static bool initialized;

    /// <summary>
    /// Initializes the texture manager. Must be called after GL context is ready.
    /// </summary>
    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        
        spriteShader = new Shader("Shaders/sprite.vert", "Shaders/sprite.frag");
        worldItemShader = new Shader("Shaders/standard.vert", "Shaders/standard-alpha.frag");
        LoadItemDescriptors();
    }

    /// <summary>
    /// Checks if the given item is a block.
    /// </summary>
    public static bool IsBlockItem(GameObjectId item) => GameContentRegistry.IsBlock(item);

    /// <summary>
    /// Gets the material for rendering an item.
    /// </summary>
    public static Material GetMaterial(GameObjectId item) => GetWorldMaterial(item);

    /// <summary>
    /// Gets/creates a material suitable for 3D world rendering (SceneNode meshes).
    /// Uses a shader that preserves diffuse alpha so transparent item textures don't render as black quads.
    /// </summary>
    public static Material GetWorldMaterial(GameObjectId item)
    {
        if (!initialized)
        {
            Initialize();
        }

        var shader = worldItemShader!;
        var key = (item, shader.Handle);
        if (materials.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (!descriptors.TryGetValue(item, out var descriptor))
        {
            Log.Warn($"ItemTextureManager: No descriptor for {item}, returning default material");
            return defaultMaterial;
        }

        var material = Material.Create(
            shader: shader,
            textureDescriptor: descriptor,
            diffuseColor: Vector3.One);

        materials[key] = material;
        return material;
    }

    /// <summary>
    /// Creates a new material for a sprite by copying texture handles from the item material.
    /// Creates a new material with a unique ID to ensure proper renderer caching.
    /// </summary>
    /// <param name="item">The item whose textures should be used.</param>
    /// <param name="shader">The shader to use for the new material.</param>
    /// <returns>A new Material instance with the item's textures and the provided shader.</returns>
    public static Material CreateMaterialForItem(GameObjectId item, Shader shader)
    {
        // Ensure initialized (late init if Initialize() wasn't called)
        if (!initialized)
        {
            Initialize();
        }
        
        // Always use the sprite shader because Sprite.OnDraw sets sprite-specific uniforms
        // (sourceFrame, tint) that must exist in the program.
        shader = spriteShader!;

        var key = (item, shader.Handle);
        if (materials.TryGetValue(key, out var existing))
        {
            return existing;
        }

        if (!descriptors.TryGetValue(item, out var descriptor))
        {
            Log.Warn($"ItemTextureManager: No descriptor for {item}, returning default material");
            return defaultMaterial;
        }

        Log.Info($"ItemTextureManager: Creating material for {item}");
        var material = Material.Create(
            shader: shader,
            textureDescriptor: descriptor,
            diffuseColor: Vector3.One);

        materials[key] = material;
        return material;
    }

    private static void LoadItemDescriptors()
    {
        var loadedCount = 0;
        var missingItems = new List<GameObjectId>();

        // Get all registered objects from the single source of truth
        foreach (var (objectId, objectDef) in GameContentRegistry.Objects)
        {
            if (objectId == GameObjectId.Air) continue;

            var texturePath = GameContentRegistry.ResolveTexturePath(objectId);
            if (texturePath == null || !File.Exists(texturePath))
            {
                missingItems.Add(objectId);
                continue;
            }

            var descriptor = new TextureDescriptor(
                texturePath,
                MinFilter: TextureMinFilter.Nearest,
                MagFilter: TextureMagFilter.Nearest,
                TextureWrapS: TextureWrapMode.ClampToEdge,
                TextureWrapT: TextureWrapMode.ClampToEdge
            );

            descriptors[objectId] = descriptor;
            loadedCount++;
        }

        // Log warnings for missing textures (only non-block items)
        var missingNonBlocks = missingItems
            .Where(id => !GameContentRegistry.IsBlock(id))
            .ToList();
        if (missingNonBlocks.Count > 0)
        {
            Log.Warn($"ItemTextureManager: Missing textures for {missingNonBlocks.Count} pure items:");
            foreach (var item in missingNonBlocks)
            {
                var expectedPath = GameContentRegistry.ResolveTexturePath(item);
                Log.Warn($"  - {item} (expected: {expectedPath})");
            }
        }

        Log.Info($"ItemTextureManager: Loaded {loadedCount} item materials successfully");
    }

}
