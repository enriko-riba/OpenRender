using OpenRender;
using OpenRender.Core;
using OpenTK.Graphics.OpenGL4;

namespace SpyroGame.World;

/// <summary>
/// Manages block textures using a 2D texture array.
/// Each BlockId maps directly to a texture array layer containing a 150×50 atlas (top|bottom|side).
/// This replaces the per-biome texture system with a simpler BlockId-based approach.
/// </summary>
/// <remarks>
/// Architecture (per MINECRAFT_TERRAIN_ARCHITECTURE.md):
/// - Each block type has ONE 150×50 atlas with Top(0-49)|Bottom(50-99)|Side(100-149) regions
/// - BlockId's lower 10 bits = texture array layer index
/// - Biome determines which BlockId is placed, NOT which texture to use
/// - Shader samples: texture(uBlockTextures, vec3(uv, blockId))
/// </remarks>
public sealed class BlockTextureManager : IDisposable
{
    private int textureArray;
    private int sampler;
    private bool disposed;

    /// <summary>
    /// Width of each block atlas (3 × 50 pixel sub-images).
    /// </summary>
    public const int AtlasWidth = 150;

    /// <summary>
    /// Height of each block atlas.
    /// </summary>
    public const int AtlasHeight = 50;

    /// <summary>
    /// Maximum number of block types supported (BlockId uses 10 bits = 1024).
    /// </summary>
    public const int MaxBlockTypes = 256; // Start smaller, expand as needed

    /// <summary>
    /// Gets the OpenGL texture array handle.
    /// </summary>
    public int TextureArray => textureArray;

    /// <summary>
    /// Gets the sampler handle for texture sampling.
    /// </summary>
    public int Sampler => sampler;

    /// <summary>
    /// Directory containing block texture atlases.
    /// </summary>
    private const string BlockTextureDir = "Resources/voxel/blocks";

    /// <summary>
    /// Initializes the block texture system.
    /// Creates a 2D texture array and loads all available block textures.
    /// </summary>
    public void Initialize()
    {
        // Create sampler with pixelated look (nearest filtering) and repeat wrap
        sampler = GL.GenSampler();
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapNearest);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

        // Create 2D texture array
        textureArray = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2DArray, textureArray);

        // Allocate storage for all layers
        // Using 4 mipmap levels for 50×50 base size
        GL.TexStorage3D(TextureTarget3d.Texture2DArray, 4, SizedInternalFormat.Rgba8, AtlasWidth, AtlasHeight, MaxBlockTypes);

        // Load block textures
        LoadBlockTextures();

        // Generate mipmaps
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2DArray);

        GL.BindTexture(TextureTarget.Texture2DArray, 0);

        Log.Info($"BlockTextureManager: Initialized texture array ({AtlasWidth}×{AtlasHeight}×{MaxBlockTypes})");
    }

    /// <summary>
    /// Loads all block textures from the Resources/voxel/blocks directory.
    /// Texture files should be named by their BlockId number, e.g., "11.png" for Grass.
    /// </summary>
    private void LoadBlockTextures()
    {
        var loadedCount = 0;

        // Load default/fallback texture for missing blocks
        LoadFallbackTexture();

        // Map of BlockId -> texture file path (customizable)
        // These paths are relative to the working directory
        var blockTexturePaths = new Dictionary<BlockId, string>
        {
            // Stone variants
            { BlockId.Stone, "Resources/voxel/blocks/stone.png" },
            { BlockId.Bedrock, "Resources/voxel/blocks/bedrock.png" },
            { BlockId.Cobblestone, "Resources/voxel/blocks/cobblestone.png" },

            // Dirt/Grass variants
            { BlockId.Dirt, "Resources/voxel/blocks/dirt.png" },
            { BlockId.Grass, "Resources/voxel/blocks/grass.png" },
            { BlockId.GrassSnowy, "Resources/voxel/blocks/grass_snowy.png" },
            { BlockId.Podzol, "Resources/voxel/blocks/podzol.png" },
            { BlockId.CoarseDirt, "Resources/voxel/blocks/coarse_dirt.png" },

            // Sand variants
            { BlockId.Sand, "Resources/voxel/blocks/sand.png" },
            { BlockId.Sandstone, "Resources/voxel/blocks/sandstone.png" },

            // Gravel/Clay
            { BlockId.Gravel, "Resources/voxel/blocks/gravel.png" },
            { BlockId.Clay, "Resources/voxel/blocks/clay.png" },

            // Snow/Ice
            { BlockId.Snow, "Resources/voxel/blocks/snow.png" },
            { BlockId.SnowDirt, "Resources/voxel/blocks/snow_dirt.png" },
            { BlockId.Ice, "Resources/voxel/blocks/ice.png" },

            // Water (special - uses separate rendering but needs texture)
            { BlockId.Water, "Resources/voxel/blocks/water.png" },
        };

        foreach (var (blockId, path) in blockTexturePaths)
        {
            if (LoadBlockTexture(blockId, path))
            {
                loadedCount++;
            }
        }

        Log.Info($"BlockTextureManager: Loaded {loadedCount} block textures");
        
        // Warn about any BlockId values that don't have textures defined
        WarnMissingBlockTextures(blockTexturePaths);
    }
    
    /// <summary>
    /// Logs warnings for any BlockId values that don't have texture mappings.
    /// This helps identify blocks that will show the fallback (magenta checkerboard) texture.
    /// </summary>
    private static void WarnMissingBlockTextures(Dictionary<BlockId, string> loadedTextures)
    {
        // Get all defined BlockId values (excluding flag combinations)
        var allBlockIds = Enum.GetValues<BlockId>()
            .Where(b => !IsFlagOnly(b) && b != BlockId.Air) // Skip Air and pure flags
            .ToList();
        
        var missingTextures = allBlockIds
            .Where(b => !loadedTextures.ContainsKey(b))
            .ToList();
        
        if (missingTextures.Count > 0)
        {
            Log.Warn($"BlockTextureManager: {missingTextures.Count} block type(s) have no texture defined (will show fallback):");
            foreach (var blockId in missingTextures)
            {
                Log.Warn($"  - {blockId} (ID={blockId.GetId()})");
            }
        }
    }
    
    /// <summary>
    /// Checks if a BlockId value is a pure flag (not a real block type).
    /// </summary>
    private static bool IsFlagOnly(BlockId blockId)
    {
        // Pure flags have no ID component (lower 10 bits are 0) and are power-of-2 values >= 1024
        var rawValue = (ushort)blockId;
        return rawValue >= 1024 && (rawValue & (rawValue - 1)) == 0;
    }

    /// <summary>
    /// Loads a single block texture into the texture array.
    /// </summary>
    /// <param name="blockId">The BlockId to load texture for.</param>
    /// <param name="path">Path to the 150×50 atlas image.</param>
    /// <returns>True if loaded successfully.</returns>
    private bool LoadBlockTexture(BlockId blockId, string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"BlockTextureManager: Missing texture for {blockId} at '{path}'");
            return false;
        }

        try
        {
            // Load image using SixLabors.ImageSharp
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(path);

            // Validate dimensions
            if (image.Width != AtlasWidth || image.Height != AtlasHeight)
            {
                Log.Warn($"BlockTextureManager: Texture '{path}' has wrong size ({image.Width}×{image.Height}), expected {AtlasWidth}×{AtlasHeight}");
                return false;
            }

            // Extract pixel data
            var pixels = new byte[AtlasWidth * AtlasHeight * 4];
            image.CopyPixelDataTo(pixels);

            // Upload to texture array layer
            var layer = blockId.GetId();
            GL.BindTexture(TextureTarget.Texture2DArray, textureArray);
            GL.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, layer, AtlasWidth, AtlasHeight, 1,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"BlockTextureManager: Failed to load texture '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Loads a magenta fallback texture for missing blocks.
    /// Uploads to ALL layers so any block without a loaded texture shows the fallback.
    /// </summary>
    private void LoadFallbackTexture()
    {
        // Create a magenta/black checkerboard pattern for missing textures
        var pixels = new byte[AtlasWidth * AtlasHeight * 4];

        for (var y = 0; y < AtlasHeight; y++)
        {
            for (var x = 0; x < AtlasWidth; x++)
            {
                var i = (y * AtlasWidth + x) * 4;
                var isCheckerDark = ((x / 8) + (y / 8)) % 2 == 0;

                if (isCheckerDark)
                {
                    pixels[i] = 255;     // R - Magenta
                    pixels[i + 1] = 0;   // G
                    pixels[i + 2] = 255; // B
                    pixels[i + 3] = 255; // A
                }
                else
                {
                    pixels[i] = 0;       // R - Black
                    pixels[i + 1] = 0;   // G
                    pixels[i + 2] = 0;   // B
                    pixels[i + 3] = 255; // A
                }
            }
        }

        // Upload fallback to ALL layers so any block without a loaded texture shows it
        GL.BindTexture(TextureTarget.Texture2DArray, textureArray);
        for (var layer = 0; layer < MaxBlockTypes; layer++)
        {
            GL.TexSubImage3D(TextureTarget.Texture2DArray, 0, 0, 0, layer, AtlasWidth, AtlasHeight, 1,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        }
        
        Log.Info($"BlockTextureManager: Initialized {MaxBlockTypes} layers with fallback texture");
    }

    /// <summary>
    /// Binds the block texture array to the specified texture unit.
    /// </summary>
    public void Bind(int textureUnit = 0)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + textureUnit);
        GL.BindTexture(TextureTarget.Texture2DArray, textureArray);
        GL.BindSampler(textureUnit, sampler);
    }

    /// <summary>
    /// Unbinds the texture array.
    /// </summary>
    public void Unbind(int textureUnit = 0)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + textureUnit);
        GL.BindTexture(TextureTarget.Texture2DArray, 0);
        GL.BindSampler(textureUnit, 0);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        if (textureArray != 0)
        {
            GL.DeleteTexture(textureArray);
            textureArray = 0;
        }

        if (sampler != 0)
        {
            GL.DeleteSampler(sampler);
            sampler = 0;
        }
    }
}
