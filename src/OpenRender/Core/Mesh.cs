using OpenRender.Core.Rendering;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a vertex buffer and draw mode.
/// </summary>
public readonly struct Mesh
{
    public Mesh()
    {
        Vao = new VertexArrayObject();
    }
    public Mesh(VertexBuffer vertexBuffer) : this()
    {
        Vao.AddBuffer(vertexBuffer);
    }   

    public readonly VertexArrayObject Vao;
    //  TODO: this struct makes only sense if multiple sub-meshes will be supported.

    override public string ToString() => $"Mesh: {Vao}";
}
