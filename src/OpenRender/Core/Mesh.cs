using OpenRender.Core.Rendering;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a vertex buffer and draw mode.
/// </summary>
public readonly struct Mesh
{
    public Mesh(VertexBuffer vertexBuffer)
        : this(vertexBuffer, vertexBuffer.Indices == null ? DrawMode.Primitive : DrawMode.Indexed) { }

    public Mesh(VertexBuffer vertexBuffer, DrawMode drawMode)
    {
        VertexBuffer = vertexBuffer;
        DrawMode = drawMode;
    }

    public readonly DrawMode DrawMode;

    public readonly VertexBuffer VertexBuffer;

    //  TODO: this struct makes only sense if multiple sub-meshes will be supported.
}
