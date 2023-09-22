using OpenRender.Core.Rendering;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a vertex buffer and draw mode.
/// </summary>
public readonly struct Mesh
{
    public Mesh(VertexArrayObject vao)
    {
        Vao = vao;
    }

    public Mesh(IVertexBuffer vertexBuffer)
        : this(vertexBuffer, vertexBuffer.Indices == null ? DrawMode.Primitive : DrawMode.Indexed) { }

    public Mesh(IVertexBuffer vertexBuffer, DrawMode drawMode)
    {
        VertexBuffer = vertexBuffer;
        DrawMode = drawMode;
    }

    public readonly DrawMode DrawMode;

    public readonly IVertexBuffer VertexBuffer;

    public readonly VertexArrayObject Vao;
    //  TODO: this struct makes only sense if multiple sub-meshes will be supported.
}
