using OpenTK.Graphics.OpenGL4;
using StbImageSharp;
using PixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace OpenRender.Core.Textures;

/// <summary>
/// Helper class to load and store textures.
/// </summary>
public class Texture
{
    private static readonly Dictionary<string, Texture> textureCache = new();

    private Texture(int glHandle)
    {
        Handle = glHandle;
    }

    public int Handle { get; init; }

    /// <summary>
    /// Source path of image - for debugging.
    /// </summary>
    public string Name { get; set; } = default!;

    /// <summary>
    /// The name of the sampler uniform that will be bound to the texture unit when setting up the shader program.
    /// </summary>
    public string UniformName { get; set; } = default!;  

    public TextureMinFilter MinFilter { get; set; }

    public TextureMagFilter MagFilter { get; set; }

    /// <summary>
    /// X axis wrap mode.
    /// </summary>
    public TextureWrapMode TextureWrapS { get; set; }

    /// <summary>
    /// Y axis wrap mode.
    /// </summary>
    public TextureWrapMode TextureWrapT { get; set; }

    public TextureTarget TextureTarget { get; set; }

    /// <summary>
    /// The texture width in pixels.
    /// </summary>
    public int Width { get; internal set; }

    /// <summary>
    /// The texture height in pixels.
    /// </summary>
    public int Height { get; internal set; }

    /// <summary>
    /// Internal cache key.
    /// </summary>
    public string CacheKey { get; private set; } = default!;

    /// <summary>
    /// Activates and binds the texture to the given unit.
    /// </summary>
    /// <param name="unit"></param>
    public void Use(TextureUnit unit) => GL.BindTextureUnit(unit - TextureUnit.Texture0, Handle);

    public override string ToString() => $"{Handle} '{Name}' : '{UniformName}' : {TextureTarget}";

    public static Texture FromByteArray(byte[] buffer,
        int width,
        int height,
        string name,
        TextureType textureType = TextureType.Unknown,
        TextureMinFilter minFilter = TextureMinFilter.Linear,
        TextureMagFilter magFilter = TextureMagFilter.Linear,
        TextureWrapMode textureWrapS = TextureWrapMode.Repeat,
        TextureWrapMode textureWrapT = TextureWrapMode.Repeat,
        bool generateMipMap = false,
        TextureTarget textureTarget = TextureTarget.Texture2D)
    {
        var key = CalculateKey(new string[] { name }, textureType, minFilter, magFilter, textureWrapS, textureWrapT, generateMipMap, textureTarget);
        if (textureCache.TryGetValue(key, out var cachedTexture))
        {
            return cachedTexture;
        }

        GL.CreateTextures(textureTarget, 1, out int handle);
        GL.TextureParameter(handle, TextureParameterName.TextureWrapS, (int)textureWrapS);
        GL.TextureParameter(handle, TextureParameterName.TextureWrapT, (int)textureWrapT);
        GL.TextureParameter(handle, TextureParameterName.TextureMinFilter, (int)minFilter);
        GL.TextureParameter(handle, TextureParameterName.TextureMagFilter, (int)magFilter);

        var mipmapLevels = 1;
        if (generateMipMap)
        {
            mipmapLevels = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        }
        GL.TextureStorage2D(handle, mipmapLevels, SizedInternalFormat.Rgba8, width, height);
        GL.TextureSubImage2D(handle, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, buffer);

        if (generateMipMap)
        {
            GL.GenerateTextureMipmap(handle);
        }
        var texture = new Texture(handle)
        {
            Name = name,
            UniformName = $"texture_{textureType.ToString().ToLowerInvariant()}",
            MinFilter = minFilter,
            MagFilter = magFilter,
            TextureWrapS = textureWrapS,
            TextureWrapT = textureWrapT,
            TextureTarget = textureTarget,
            Width = width,
            Height = height,
            CacheKey = key
        };
        textureCache[key] = texture;
        GL.ObjectLabel(ObjectLabelIdentifier.Texture, handle, -1, name);
        Log.CheckGlError();

        Log.Info($"created texture: {texture}");
        return texture;
    }

    public static Texture FromDescriptor(TextureDescriptor descriptor) => FromFile(
            descriptor.Paths,
            descriptor.TextureType,
            descriptor.MinFilter,
            descriptor.MagFilter,
            descriptor.TextureWrapS,
            descriptor.TextureWrapT,
            descriptor.GenerateMipMap,
            descriptor.TextureTarget
    );

    public static Texture FromFile(string[] paths,
        TextureType textureType = TextureType.Unknown,
        TextureMinFilter minFilter = TextureMinFilter.LinearMipmapLinear,
        TextureMagFilter magFilter = TextureMagFilter.Linear,
        TextureWrapMode textureWrapS = TextureWrapMode.Repeat,
        TextureWrapMode textureWrapT = TextureWrapMode.Repeat,
        bool generateMipMap = true,
        TextureTarget textureTarget = TextureTarget.Texture2D)
    {
        var key = CalculateKey(paths, textureType, minFilter, magFilter, textureWrapS, textureWrapT, generateMipMap, textureTarget);
        if (textureCache.TryGetValue(key, out var cachedTexture))
        {
            return cachedTexture;
        }

        GL.CreateTextures(textureTarget, 1, out int handle);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);

        GL.TextureParameterI(handle, TextureParameterName.TextureWrapS, new[] { (int)textureWrapS });
        GL.TextureParameterI(handle, TextureParameterName.TextureWrapT, new[] { (int)textureWrapT });
        GL.TextureParameterI(handle, TextureParameterName.TextureMinFilter, new[] { (int)minFilter });
        GL.TextureParameterI(handle, TextureParameterName.TextureMagFilter, new[] { (int)magFilter });


        ImageResult? image = null;
        if (textureTarget == TextureTarget.Texture2D)
        {
            // OpenGL has it's texture origin in the lower left corner instead of the top left corner,
            // so we tell StbImageSharp to flip the image when loading.
            StbImage.stbi_set_flip_vertically_on_load(1);

            using var stream = File.OpenRead(paths[0]);
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            var mipmapLevels = 1;
            if (generateMipMap)
            {
                mipmapLevels = 1 + (int)Math.Floor(Math.Log2(Math.Max(image.Width, image.Height)));
            }
            GL.TextureStorage2D(handle, mipmapLevels, SizedInternalFormat.Srgb8Alpha8, image.Width, image.Height);
            GL.TextureSubImage2D(handle, 0, 0, 0, image.Width, image.Height, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);

            if (generateMipMap)
            {
                GL.GenerateTextureMipmap(handle);
            }
        }
        else if (textureTarget == TextureTarget.TextureCubeMap && paths.Length == 6)
        {
            StbImage.stbi_set_flip_vertically_on_load(0);
            for (var face = 0; face < paths.Length; face++)
            {
                using var stream = File.OpenRead(paths[face]);
                image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                if (face == 0) GL.TextureStorage2D(handle, 1, SizedInternalFormat.Srgb8, image.Width, image.Height);
                GL.TextureSubImage3D(handle, 0, 0, 0, face, image.Width, image.Height, 1, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);
            }
        }

        var texture = new Texture(handle)
        {
            Name = paths[0],
            UniformName = $"texture_{textureType.ToString().ToLowerInvariant()}",
            MinFilter = minFilter,
            MagFilter = magFilter,
            TextureWrapS = textureWrapS,
            TextureWrapT = textureWrapT,
            TextureTarget = textureTarget,
            Width = image!.Width,
            Height = image.Height,
            CacheKey = key
        };
        textureCache[key] = texture;
        GL.ObjectLabel(ObjectLabelIdentifier.Texture, handle, -1, texture.Name);
        Log.CheckGlError();
        Log.Info($"created texture: {texture}");
        return texture;
    }

    /// <summary>
    /// Creates an array of texture objects based on the input <see cref="Material"/>.
    /// </summary>
    /// <param name="material"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static Texture[] CreateFromMaterial(Material material)
    {
        var textures = new Texture[material.TextureDescriptors?.Length ?? 0];
        if (material.TextureDescriptors?.Any() ?? false)
        {
            var diffuseCount = 0;
            var detailCount = 0;
            var normalCount = 0;
            var specularCount = 0;
            var additionalCount = 0;
            var cubeMapCount = 0;
            var counter = 0;
            foreach (var textureDescriptor in material.TextureDescriptors!)
            {
                switch (textureDescriptor.TextureType)
                {
                    case TextureType.Diffuse:
                    case TextureType.Unknown:
                        if (diffuseCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple diffuse textures supplied!");
                        }
                        break;

                    case TextureType.Detail:
                        if (detailCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple detail textures supplied!");
                        }
                        break;
                    case TextureType.Normal:
                        if (normalCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple normal textures supplied!");
                        }
                        break;

                    case TextureType.Specular:
                        if (specularCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple specular textures supplied!");
                        }
                        break;

                    case TextureType.Additional1:
                        if (additionalCount++ > 3)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Max 4 additional textures are supported!");
                        }
                        break;
                    case TextureType.Additional2:
                        if (additionalCount++ > 3)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Max 4 additional textures are supported!");
                        }
                        break;
                    case TextureType.Additional3:
                        if (additionalCount++ > 3)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Max 4 additional textures are supported!");
                        }
                        break;
                    case TextureType.Additional4:
                        if (additionalCount++ > 3)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Max 4 additional textures are supported!");
                        }
                        break;
                    case TextureType.CubeMap:
                        if (cubeMapCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple cube map textures supplied!");
                        }
                        break;
                }
                var t = FromDescriptor(textureDescriptor);
                textures[counter++] = t;
            }
        }
        return textures;
    }

    private static string CalculateKey(string[] paths,
        TextureType textureType,
        TextureMinFilter minFilter,
        TextureMagFilter magFilter,
        TextureWrapMode textureWrapS,
        TextureWrapMode textureWrapT,
        bool generateMipMap,
        TextureTarget textureTarget = TextureTarget.Texture2D)
    {
        var names = textureTarget == TextureTarget.TextureCubeMap ? string.Join(':', paths) : paths[0];
        return $"{names}|{textureType}:{minFilter}:{magFilter}:{textureWrapS}:{textureWrapT}:{generateMipMap}";
    }
}