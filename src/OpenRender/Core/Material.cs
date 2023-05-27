using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenTK.Mathematics;

namespace OpenRender.Core;

public struct Material
{
    public const int MaxTextures = 8;

    private static uint counter;
    private bool isInitialized;
    private int[]? textureHandles;

    public Material()
    {
    }


    public uint Id { get; init; }

    public readonly bool IsInitialized => isInitialized;

    public readonly IEnumerable<int> TextureHandles => textureHandles ?? Enumerable.Empty<int>();

    public readonly bool HasDiffuse => TextureDescriptors?.Any(ti => ti?.TextureType == TextureType.Diffuse) ?? false;

    public TextureDescriptor[]? TextureDescriptors { get; init; }

    public Texture[]? Textures { get; private set; }

    /// <summary>
    /// Shader program used to render the object.
    /// </summary>
    public Shader? Shader { get; set; }

    /// <summary>
    /// Scaling factor of the detail texture. Has no impact without detail texture. 
    /// If 0 the detail texture is not applied even if it is defined. 
    /// Values < 1 are stretching the detail texture while values > 1 are repeating the detail texture over the object surface.
    /// </summary>
    public float DetailTextureFactor { get; init; }

    /// <summary>
    /// Diffuse color multiplied with light color.
    /// </summary>
    public Vector3 DiffuseColor { get; init; } = Vector3.One;

    /// <summary>
    /// Specular color multiplied with light color. This should usually be white.
    /// </summary>
    public Vector3 SpecularColor { get; init; } = Vector3.One;

    /// <summary>
    /// Object shininess, used with specular color.
    /// </summary>
    public float Shininess { get; init; } = 0.1f;

    /// <summary>
    /// Color emitted from the object surface. This color is for self luminating objects, scene lights have no effect on emissive color.
    /// </summary>
    public Vector3 EmissiveColor { get; init; } = Vector3.Zero;

    public void Initialize()
    {
        Textures = Texture.CreateFromMaterial(this);
        textureHandles = Textures.Select(t => t.Handle).ToArray();
        isInitialized = true;
    }

    public static Material Create(Shader? shader, TextureDescriptor[]? textureDescriptors, Vector3 diffuseColor, Vector3 emissiveColor, Vector3 specularColor, float shininess = 0, float detailTextureFactor = 0f)
    {
        if ((textureDescriptors?.Length ?? 0) > MaxTextures) throw new ArgumentOutOfRangeException(nameof(textureDescriptors));
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
        return mat;
    }
    
    public static Material Create(TextureDescriptor[]? textureDescriptors, Vector3 diffuseColor, Vector3 specularColor, float shininess = 0, float detailTextureFactor = 0f)
    {
        return Create(null, textureDescriptors, diffuseColor, Vector3.Zero, specularColor, shininess, detailTextureFactor);
    }

    public static Material Create(Shader? shader, TextureDescriptor[]? textureDescriptors, float shininess = 0f, float detailTextureFactor = 0f)
    {
        return Create(shader, textureDescriptors, Vector3.One, Vector3.Zero, Vector3.One, shininess, detailTextureFactor);
    }

    public static Material Create(Shader shader, TextureDescriptor textureDescriptor, float shininess = 0f, float detailTextureFactor = 0f)
    {
        return Create(shader, new TextureDescriptor[] { textureDescriptor }, shininess, detailTextureFactor);
    }
    public static Material Create(TextureDescriptor textureDescriptor, float shininess = 0f, float detailTextureFactor = 0f)
    {
        return Create(null, new TextureDescriptor[] { textureDescriptor }, shininess, detailTextureFactor);
    }

    public static Material Create(TextureDescriptor[] textureDescriptors, float shininess = 0f, float detailTextureFactor = 0f)
    {
        return Create(null, textureDescriptors, shininess, detailTextureFactor);
    }
    public static Material Create(float shininess, float detailTextureFactor = 0f)
    {
        return Create(null, Array.Empty<TextureDescriptor>(), shininess, detailTextureFactor);
    }
}
