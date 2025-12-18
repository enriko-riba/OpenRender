using OpenRender;
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

        // Use NearestMipmapLinear to reduce aliasing/shimmering at distance while keeping pixelated look
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapLinear);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        // Clamp the atlas to avoid wrap bleeding at UV edges (especially noticeable on cutout vegetation).
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        // Enable Anisotropic Filtering if supported (greatly improves ground texture quality at angles)
        var maxAniso = GL.GetFloat((GetPName)0x84FF); // GL_MAX_TEXTURE_MAX_ANISOTROPY_EXT
        if (maxAniso > 1.0f)
        {
            GL.SamplerParameter(sampler, (SamplerParameterName)0x84FE, Math.Min(maxAniso, 16.0f)); // GL_TEXTURE_MAX_ANISOTROPY_EXT
        }

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
    /// Texture filenames are derived from BlockId enum names using snake_case convention:
    /// e.g., GrassSnowy -> grass_snowy.png, OakLog -> oak_log.png
    /// </summary>
    private void LoadBlockTextures()
    {
        var loadedCount = 0;
        var missingBlocks = new List<BlockId>();

        // Load default/fallback texture for missing blocks
        LoadFallbackTexture();

        // Get all block types (excluding Air which has no texture)
        var allBlockIds = Enum.GetValues<BlockId>()
            .Where(b => b != BlockId.Air)
            .ToList();

        foreach (var blockId in allBlockIds)
        {
            var texturePath = GetTexturePathForBlock(blockId);

            if (File.Exists(texturePath))
            {
                if (LoadBlockTexture(blockId, texturePath))
                {
                    loadedCount++;
                }
            }
            else
            {
                missingBlocks.Add(blockId);
            }
        }

        // Log warnings for missing textures
        if (missingBlocks.Count > 0)
        {
            Log.Warn($"BlockTextureManager: Missing textures for {missingBlocks.Count} block types (will show fallback):");
            foreach (var block in missingBlocks)
            {
                var expectedPath = GetTexturePathForBlock(block);
                Log.Warn($"  - {block} (expected: {expectedPath})");
            }
        }

        Log.Info($"BlockTextureManager: Loaded {loadedCount} block textures successfully");
    }

    /// <summary>
    /// Texture aliases for blocks that share textures with other blocks.
    /// Key: BlockId that needs aliasing, Value: BlockId whose texture to use
    /// </summary>
    private static readonly Dictionary<BlockId, BlockId> TextureAliases = new()
    {
        { BlockId.WallTorch, BlockId.Torch },  // Wall torch uses same texture as regular torch
    };

    /// <summary>
    /// Converts a BlockId enum name to a texture file path using snake_case convention.
    /// e.g., GrassSnowy -> Resources/voxel/blocks/grass_snowy.png
    /// Supports texture aliases for blocks that share textures.
    /// </summary>
    private static string GetTexturePathForBlock(BlockId blockId)
    {
        // Check for texture alias first
        if (TextureAliases.TryGetValue(blockId, out var aliasedBlock))
        {
            blockId = aliasedBlock;
        }

        var enumName = blockId.ToString();
        var snakeCaseName = PascalToSnakeCase(enumName);
        return $"{BlockTextureDir}/{snakeCaseName}.png";
    }

    /// <summary>
    /// Converts PascalCase to snake_case.
    /// e.g., "GrassSnowy" -> "grass_snowy", "OakLog" -> "oak_log"
    /// </summary>
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
                // Add underscore before uppercase letters (except at start)
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
    public static void Unbind(int textureUnit = 0)
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
