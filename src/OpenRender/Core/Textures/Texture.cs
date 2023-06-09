﻿using OpenTK.Graphics.OpenGL4;
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

    /// <summary>
    /// Preferred texture unit this texture is bound to.
    /// Note: the texture unit is set based on the <see cref="TextureDescriptor.TextureType"/> but cen be changed by the <see cref="TextureBatcher"/> or manually.
    /// </summary>
    public TextureUnit TextureUnit { get; set; }

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
    /// Internal cache key.
    /// </summary>
    public string CacheKey { get; private set; } = default!;

    /// <summary>
    /// Activates and binds the texture to the given unit.
    /// </summary>
    /// <param name="unit"></param>
    public void Use(TextureUnit unit)
    {
        GL.ActiveTexture(unit);
        GL.BindTexture(TextureTarget, Handle);
    }

    /// <summary>
    /// Activates and binds the texture to the <see cref="TextureUnit"/> unit.
    /// </summary>
    public void Use() => Use(TextureUnit);

    public override string ToString() => $"[{Handle}] '{Name}' : '{UniformName}' : {TextureTarget}";

    public static Texture FromDescriptor(TextureDescriptor descriptor, TextureUnit unit)
    {
        return FromFile(
            descriptor.Paths,
            unit,
            descriptor.TextureType,
            descriptor.MinFilter,
            descriptor.MagFilter,
            descriptor.TextureWrapS,
            descriptor.TextureWrapT,
            descriptor.GenerateMipMap,
            descriptor.TextureTarget
            );
    }

    public static Texture FromFile(string[] paths,
        TextureUnit unit = TextureUnit.Texture0,
        TextureType textureType = TextureType.Unknown,
        TextureMinFilter minFilter = TextureMinFilter.Linear,
        TextureMagFilter magFilter = TextureMagFilter.Linear,
        TextureWrapMode textureWrapS = TextureWrapMode.Repeat,
        TextureWrapMode textureWrapT = TextureWrapMode.Repeat,
        bool generateMipMap = true,
        TextureTarget textureTarget = TextureTarget.Texture2D)
    {
        var key = CalculateKey(paths, textureType, minFilter, magFilter, textureWrapS, textureWrapT, generateMipMap, textureTarget );
        if(textureCache.TryGetValue(key, out var cachedTexture))
        {
            return cachedTexture;
        }

        var handle = GL.GenTexture();
        GL.BindTexture(textureTarget, handle);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);

        if (textureTarget == TextureTarget.Texture2D)
        {
            // OpenGL has it's texture origin in the lower left corner instead of the top left corner,
            // so we tell StbImageSharp to flip the image when loading.
            StbImage.stbi_set_flip_vertically_on_load(1);

            using var stream = File.OpenRead(paths[0]);            
            var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            //   Note: param 'border' must always be 0; it's a legacy parameter that Khronos never got rid of.         
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.SrgbAlpha, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);
            if (generateMipMap)
            {
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            }
        }
        else if (textureTarget == TextureTarget.TextureCubeMap && paths.Length == 6)
        {
            StbImage.stbi_set_flip_vertically_on_load(0);
            for (var i = 0; i < paths.Length; i++)
            {
                using var stream = File.OpenRead(paths[i]);
                var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
                GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + i, 0, PixelInternalFormat.SrgbAlpha, image.Width, image.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, image.Data);
            }
            GL.TexParameter(textureTarget, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        }

        //  S is for the X axis, and T is for the Y axis.
        GL.TexParameter(textureTarget, TextureParameterName.TextureWrapS, (int)textureWrapS);
        GL.TexParameter(textureTarget, TextureParameterName.TextureWrapT, (int)textureWrapT);
        GL.TexParameter(textureTarget, TextureParameterName.TextureMinFilter, (int)minFilter);
        GL.TexParameter(textureTarget, TextureParameterName.TextureMagFilter, (int)magFilter);

        var texture = new Texture(handle)
        {
            TextureUnit = unit,
            Name = paths[0],
            UniformName = $"texture_{textureType.ToString().ToLowerInvariant()}",
            MinFilter = minFilter,
            MagFilter = magFilter,
            TextureWrapS = textureWrapS,
            TextureWrapT = textureWrapT,
            TextureTarget = textureTarget,
            CacheKey = key
        };
        textureCache[key] = texture;
        Log.Debug($"created texture: {texture}");
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
            var specularCount = 0;
            var additionalCount = 0;
            var cubeMapCount = 0;
            var counter = 0;
            foreach (var textureDescriptor in material.TextureDescriptors!)
            {
                var unit = TextureUnit.Texture0;
                switch (textureDescriptor.TextureType)
                {
                    case TextureType.Diffuse:
                    case TextureType.Unknown:
                        if (diffuseCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple diffuse textures supplied!");
                        }
                        unit = TextureUnit.Texture0;
                        break;

                    case TextureType.Detail:
                        unit = TextureUnit.Texture1;
                        if (detailCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple detail textures supplied!");
                        }
                        break;

                    case TextureType.Specular:
                        unit = TextureUnit.Texture2;
                        if (specularCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple specular textures supplied!");
                        }
                        break;

                    case TextureType.Additional1:
                        unit = TextureUnit.Texture3;
                        if (additionalCount++ > 3)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Max 4 additional textures are supported!");
                        }
                        break;
                    case TextureType.Additional2:
                        unit = TextureUnit.Texture4;
                        if (additionalCount++ > 3)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Max 4 additional textures are supported!");
                        }
                        break;
                    case TextureType.Additional3:
                        unit = TextureUnit.Texture5;
                        if (additionalCount++ > 3)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Max 4 additional textures are supported!");
                        }
                        break;
                    case TextureType.Additional4:
                        unit = TextureUnit.Texture6;
                        if (additionalCount++ > 3)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Max 4 additional textures are supported!");
                        }
                        break;
                    case TextureType.CubeMap:
                        unit = TextureUnit.Texture7;
                        if (cubeMapCount++ > 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(material), "Multiple cube map textures supplied!");
                        }
                        break;
                }
                var t = FromDescriptor(textureDescriptor, unit);
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
        var names = textureTarget == TextureTarget.TextureCubeMap ? string.Join(':',paths) : paths[0];
        return $"{names}|{textureType}:{minFilter}:{magFilter}:{textureWrapS}:{textureWrapT}:{generateMipMap}";
    }
}