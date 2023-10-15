namespace OpenRender.Core.Rendering;

public class VertexBuffer : Buffer<Vertex>
{
    public VertexBuffer(Vertex[] data) : base(data, VertexDeclarations.VertexPositionNormalTexture) 
    {
        SetLabel("Vertex VBO");
    }
}
