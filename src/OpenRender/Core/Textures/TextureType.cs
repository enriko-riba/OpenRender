namespace OpenRender.Core.Textures;

public enum TextureType
{
    /// <summary>
    /// No explicitly assigned type, this texture type is treated as diffuse.
    /// </summary>
    Unknown,

    /// <summary>
    /// Main color texture, stretched over the whole object.
    /// </summary>
    Diffuse,

    /// <summary>
    /// Detail or noise texture, tiled over the object based on the detail factor.
    /// </summary>
    Detail,

    /// <summary>
    /// Color reflection map.
    /// </summary>
    Specular,

    /// <summary>
    /// Application specific usage in custom shaders.
    /// </summary>
    Additional1,

    /// <summary>
    /// Application specific usage in custom shaders.
    /// </summary>
    Additional2,

    /// <summary>
    /// Application specific usage in custom shaders.
    /// </summary>
    Additional3,

    /// <summary>
    /// Application specific usage in custom shaders.
    /// </summary>
    Additional4,

    /// <summary>
    /// Cube map for skyboxes etc.
    /// </summary>
    CubeMap,
}
