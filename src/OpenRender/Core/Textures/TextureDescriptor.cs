using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Textures;

/// <summary>
/// Contains information needed to create a 2D texture.
/// </summary>
/// <param name="Paths"></param>
/// <param name="MinFilter"></param>
/// <param name="MagFilter"></param>
/// <param name="TextureType"></param>
/// <param name="TextureWrapS"></param>
/// <param name="TextureWrapT"></param>
/// <param name="GenerateMipMap"></param>
public record TextureDescriptor(
    string[] Paths,
    TextureMinFilter MinFilter = TextureMinFilter.Linear,
    TextureMagFilter MagFilter = TextureMagFilter.Linear,
    TextureType TextureType = TextureType.Unknown,
    TextureWrapMode TextureWrapS = TextureWrapMode.Repeat,
    TextureWrapMode TextureWrapT = TextureWrapMode.Repeat,
    bool GenerateMipMap = true,
    TextureTarget TextureTarget = TextureTarget.Texture2D)
{
    public TextureDescriptor(string path) : this(new string[] { path }) { }

    public TextureDescriptor(string path,
    TextureMinFilter MinFilter = TextureMinFilter.Linear,
    TextureMagFilter MagFilter = TextureMagFilter.Linear,
    TextureType TextureType = TextureType.Unknown,
    TextureWrapMode TextureWrapS = TextureWrapMode.Repeat,
    TextureWrapMode TextureWrapT = TextureWrapMode.Repeat,
    bool GenerateMipMap = true,
    TextureTarget TextureTarget = TextureTarget.Texture2D) : this(
        new string[] { path }, 
        MinFilter,
        MagFilter,
        TextureType,
        TextureWrapS,
        TextureWrapT,
        GenerateMipMap,
        TextureTarget) { }
};
