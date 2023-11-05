﻿using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Mathematics;
using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core;

/// <summary>
/// Defines lightning properties and the shader used to render the object.
/// </summary>
public class Material
{
    private readonly static Material defaultMaterial;

    static Material()
    {
        var sampler = Sampler.Create(TextureMinFilter.Nearest, TextureMagFilter.Nearest, TextureWrapMode.Repeat, TextureWrapMode.Repeat);
        var defaultDiffuse = TextureBase.FromFile(new string[] { "Resources/default-diffuse.png" }, false);
        var defaultDetail = TextureBase.FromFile(new string[] { "Resources/default-detail.png" }, false);
        var defaultNormal = TextureBase.FromFile(new string[] { "Resources/default-normal.png" }, false);
        var defaultSpecular = TextureBase.FromFile(new string[] { "Resources/default-specular.png" }, false);
        var defaultBump = TextureBase.FromFile(new string[] { "Resources/default-bump.png" }, false);
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
            TextureBases = new TextureBase[] { 
                defaultDiffuse, 
                defaultDetail, 
                defaultNormal, 
                defaultSpecular, 
                defaultBump, 
                defaultBump, 
                defaultBump, 
                defaultBump 
            },
            BindlessTextures = new BindlessTexture[] { 
                new(defaultDiffuse, sampler), 
                new (defaultDetail, sampler), 
                new (defaultNormal, sampler), 
                new(defaultSpecular, sampler), 
                new(defaultBump, sampler), 
                new(defaultBump, sampler), 
                new(defaultBump, sampler), 
                new(defaultBump, sampler) 
            },
        };
    }

    public static Material Default => defaultMaterial;

    public const int MaxTextures = 8;

    private static uint counter;
    private int[]? textureHandles;

    public uint Id { get; init; }

    public IEnumerable<int> TextureHandles => textureHandles ?? Enumerable.Empty<int>();

    public bool HasDiffuse => TextureDescriptors?.Any(ti => ti?.TextureType == TextureType.Diffuse) ?? false;
    public bool HasDetail => TextureDescriptors?.Any(ti => ti?.TextureType == TextureType.Detail) ?? false;
    public bool HasNormal => TextureDescriptors?.Any(ti => ti?.TextureType == TextureType.Normal) ?? false;

    public TextureDescriptor[]? TextureDescriptors { get; init; }

    public Texture[]? Textures { get; private set; }


    public TextureBase[] TextureBases { get; private set; } = new TextureBase[8];

    public BindlessTexture[] BindlessTextures { get; private set; } = new BindlessTexture[8];

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
    public float DetailTextureFactor { get; set; }

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
        Textures = Texture.CreateFromMaterial(this);
        textureHandles = Textures.Select(t => t.Handle).ToArray();
    }

    public override string ToString() => $"{Id} {string.Join(',', TextureDescriptors?.SelectMany(td => td.Paths) ?? Enumerable.Empty<string>())}";

    public static Material Create(Shader shader, TextureDescriptor[]? textureDescriptors, Vector3 diffuseColor, Vector3 emissiveColor, Vector3 specularColor, float shininess = 0, float detailTextureFactor = 0f)
    {
        var textureCount = textureDescriptors?.Length ?? 0;
        if (textureCount > MaxTextures) throw new ArgumentOutOfRangeException(nameof(textureDescriptors));
        var id = Interlocked.Increment(ref counter);
        var mat = new Material()
        {
            Shader = shader,
            TextureDescriptors = textureDescriptors,
            Shininess = shininess,
            DetailTextureFactor = detailTextureFactor,
            DiffuseColor = diffuseColor,
            SpecularColor = specularColor,
            EmissiveColor = emissiveColor,
            Id = id
        };
        mat.Initialize();
        if (diffuseColor.Length == 0 && textureCount > 0) Log.Warn("Material created with no diffuse color, that's probably not what you want!");
        return mat;
    }

    public static Material Create(Shader shader, TextureDescriptor[]? textureDescriptors, Vector3 diffuseColor, Vector3 specularColor, float shininess = 0, float detailTextureFactor = 0f) =>
        Create(shader, textureDescriptors, diffuseColor, Vector3.Zero, specularColor, shininess, detailTextureFactor);

    public static Material Create(Shader shader, TextureDescriptor[]? textureDescriptors, float shininess = 0f, float detailTextureFactor = 0f) =>
        Create(shader, textureDescriptors, Vector3.One, Vector3.Zero, Vector3.One, shininess, detailTextureFactor);

    public static Material Create(Shader shader, TextureDescriptor textureDescriptor, float shininess = 0f, float detailTextureFactor = 0f) =>
        Create(shader, new TextureDescriptor[] { textureDescriptor }, shininess, detailTextureFactor);

    public static Material Create(Shader shader, TextureDescriptor textureDescriptor, Vector3 diffuseColor, float shininess = 0f, float detailTextureFactor = 0f) =>
        Create(shader, new TextureDescriptor[] { textureDescriptor }, diffuseColor, Vector3.Zero, Vector3.One, shininess, detailTextureFactor);

}
