﻿using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core;

/// <summary>
/// Defines lightning properties, textures and shader used to render a object.
/// </summary>
public class Material
{
    private readonly static Material defaultMaterial;

    static Material()
    {
        var sampler = Sampler.Create(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.ClampToBorder, TextureWrapMode.ClampToBorder);
        var defaultDiffuse = Texture.FromFile(["Resources/default-diffuse.png"], false);
        var defaultDetail = Texture.FromFile(["Resources/default-detail.png"], false);
        var defaultNormal = Texture.FromFile(["Resources/default-normal.png"], false, isNormalMap: true);
        var defaultSpecular = Texture.FromFile(["Resources/default-specular.png"], false);
        var defaultBump = Texture.FromFile(["Resources/default-bump.png"], false);
        /*
            public long Diffuse;
            public long Detail;
            public long Normal;
            public long Specular;
            public long Bump;
            public long T6;
            public long T7;
            public long T8;
        */
        defaultMaterial = new Material()
        {
            Textures = [
                defaultDiffuse,
                defaultDetail,
                defaultNormal,
                defaultSpecular,
                defaultBump,
                defaultBump,
                defaultBump,
                defaultBump
            ],
            BindlessTextureHandles = [
                defaultDiffuse.GetBindlessHandle(sampler),
                defaultDetail.GetBindlessHandle(sampler),
                defaultNormal.GetBindlessHandle(sampler),
                defaultSpecular.GetBindlessHandle(sampler),
                defaultBump.GetBindlessHandle(sampler),
                defaultBump.GetBindlessHandle(sampler),
                defaultBump.GetBindlessHandle(sampler),
                defaultBump.GetBindlessHandle(sampler)
            ],
        };
    }

    public static Material Default => defaultMaterial;

    public const int MaxTextures = 8;

    private static uint counter;

    public uint Id { get; init; }

    public TextureDescriptor[]? TextureDescriptors { get; init; }

    public Texture[] Textures { get; private set; } = new Texture[8];

    public ulong[] BindlessTextureHandles { get; private set; } = new ulong[8];

    /// <summary>
    /// Shader program used to render the object.
    /// </summary>
    public Shader Shader { get; set; } = default!;

    #region Colors
    /// <summary>
    /// Scaling factor of the detail texture. Has no impact without detail texture. 
    /// If 0 the detail texture is not applied even if it is defined. 
    /// Values < 1 are stretching the detail texture while values > 1 are repeating the detail texture over the object surface.
    /// </summary>
    public float DetailTextureScaleFactor { get; set; }

    /// <summary>
    /// Blending factor of the detail texture. Has no impact without detail texture.
    /// </summary>
    public float DetailTextureBlendFactor { get; set; }

    /// <summary>
    /// Diffuse color multiplied with light color.
    /// </summary>
    public Vector3 DiffuseColor { get; set; } = Vector3.One;

    /// <summary>
    /// Specular color multiplied with light color. This should usually be white.
    /// </summary>
    public Vector3 SpecularColor { get; set; } = Vector3.One;

    /// <summary>
    /// Object shininess, used with specular color.
    /// </summary>
    public float Shininess { get; set; } = 0.1f;

    /// <summary>
    /// Color emitted from the object surface. This color is for self luminating objects, scene lights have no effect on emissive color.
    /// </summary>
    public Vector3 EmissiveColor { get; set; } = Vector3.Zero;
    #endregion

    private void Initialize()
    {
        foreach (var descriptor in TextureDescriptors ?? [])
        {
            var tb = Texture.FromDescriptor(descriptor);
            var sampler = Sampler.FromDescriptor(descriptor);
            if (descriptor.TextureType == TextureType.CubeMap)
            {
                Textures[0] = tb;
                BindlessTextureHandles[0] = tb.GetBindlessHandle(sampler);
            }
            else
            {
                Textures[(int)descriptor.TextureType] = tb;
                BindlessTextureHandles[(int)descriptor.TextureType] = tb.GetBindlessHandle(sampler);
            }
        }

        // fill in missing textures with default 1x1 pixel textures
        for(var i = 0; i < Textures.Length; i++)
        {
            if (Textures[i] == null)
            {
                Textures[i] = Default.Textures[i];
                BindlessTextureHandles[i] = Default.BindlessTextureHandles[i];
            }
        }
    }

    public override string ToString() => $"{Id} {string.Join(',', TextureDescriptors?.SelectMany(td => td.Paths) ?? Enumerable.Empty<string>())}";

    public static Material Create(Shader shader, TextureDescriptor[]? textureDescriptors, Vector3 diffuseColor, Vector3 emissiveColor, Vector3 specularColor, float shininess = 0, float detailTextureScaleFactor = 0f, float detailTextureBlendFactor = 0.2f)
    {
        var textureCount = textureDescriptors?.Length ?? 0;
        if (textureCount > MaxTextures) throw new ArgumentOutOfRangeException(nameof(textureDescriptors));
        var id = Interlocked.Increment(ref counter);
        var mat = new Material()
        {
            Shader = shader,
            TextureDescriptors = textureDescriptors,
            Shininess = shininess,
            DetailTextureScaleFactor = detailTextureScaleFactor,
            DetailTextureBlendFactor = detailTextureBlendFactor,
            DiffuseColor = diffuseColor,
            SpecularColor = specularColor,
            EmissiveColor = emissiveColor,
            Id = id
        };
        mat.Initialize();
        
        if (diffuseColor.Length == 0 && textureCount > 0) Log.Warn("Material created with no diffuse color, that's probably not what you want!");
        return mat;
    }

    public static Material Create(Shader shader, TextureDescriptor[]? textureDescriptors, Vector3 diffuseColor, Vector3 specularColor, float shininess = 0, float detailTextureScaleFactor = 0f, float detailTextureBlendFactor = 0.2f) =>
        Create(shader, textureDescriptors, diffuseColor, Vector3.Zero, specularColor, shininess, detailTextureScaleFactor, detailTextureBlendFactor);

    public static Material Create(Shader shader, TextureDescriptor[]? textureDescriptors, float shininess = 0f, float detailTextureScaleFactor = 0f, float detailTextureBlendFactor = 0.2f) =>
        Create(shader, textureDescriptors, Vector3.One, Vector3.Zero, Vector3.One, shininess, detailTextureScaleFactor, detailTextureBlendFactor);

    public static Material Create(Shader shader, TextureDescriptor textureDescriptor, float shininess = 0f, float detailTextureScaleFactor = 0f, float detailTextureBlendFactor = 0.2f) =>
        Create(shader, [textureDescriptor], shininess, detailTextureScaleFactor, detailTextureBlendFactor);

    public static Material Create(Shader shader, TextureDescriptor textureDescriptor, Vector3 diffuseColor, float shininess = 0f, float detailTextureScaleFactor = 0f, float detailTextureBlendFactor = 0.2f) =>
        Create(shader, [textureDescriptor], diffuseColor, Vector3.Zero, Vector3.One, shininess, detailTextureScaleFactor, detailTextureBlendFactor);

}
