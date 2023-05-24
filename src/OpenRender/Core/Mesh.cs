using OpenRender.Core.Rendering;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a vertex buffer, draw mode and material.
/// </summary>
public class Mesh
{
    private readonly DrawMode drawMode;

    public Mesh(VertexBuffer vertexBuffer, Material material) : this(
        vertexBuffer, 
        vertexBuffer.Indices == null ? DrawMode.Primitive : DrawMode.Indexed, 
        material) { }

    public Mesh(VertexBuffer vertexBuffer, DrawMode drawMode, Material material)
    {
        VertexBuffer = vertexBuffer;
        Material = material;
        this.drawMode = drawMode;
    }

    public DrawMode DrawMode => drawMode;
    public Material Material { get; set; }
    public VertexBuffer VertexBuffer { get; set; }
}
