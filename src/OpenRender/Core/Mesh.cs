using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a vertex buffer and draw mode.
/// </summary>
public struct Mesh
{
    public Mesh(VertexArrayObject vao) : this(vao, BoundingSphere.Default) { }
    public Mesh(VertexArrayObject vao, BoundingSphere boundingSphere)
    {
        Vao = vao;
        BoundingSphere = boundingSphere;
    }

    public readonly VertexArrayObject Vao;

    public BoundingSphere BoundingSphere;

    //  TODO: this struct makes only sense if multiple sub-meshes will be supported.
}
