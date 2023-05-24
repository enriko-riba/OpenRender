namespace OpenRender.Core;

/// <summary>
/// Defines how the geometry in a vertex buffer will be rendered.
/// </summary>
public enum DrawMode
{
    /// <summary>
    /// Renders using GL.DrawElements
    /// </summary>
    Indexed,

    /// <summary>
    /// Renders using GL.DrawArrays
    /// </summary>
    Primitive,
}
