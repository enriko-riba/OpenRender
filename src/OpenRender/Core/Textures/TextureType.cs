namespace OpenRender.Core.Textures;

public enum TextureType
{
    /// <summary>
    /// No explicitly assigned type, this texture type is treated as diffuse.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Main color texture, stretched over the whole object.
    /// </summary>
    Diffuse = Unknown,

    /// <summary>
    /// Detail or noise texture, tiled over the object based on the detail factor.
    /// </summary>
    Detail,

    /// <summary>
    /// Normal map added to the object surface to simulate more detail when calculating lightning.
    /// </summary>
    Normal,

    /// <summary>
    /// Color reflection map.
    /// </summary>
    Specular,

    /// <summary>
    /// Application specific usage in custom shaders.
    /// </summary>
    Bump,

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
