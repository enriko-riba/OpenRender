using OpenRender.Core.Rendering;

namespace OpenRender.Core;

public class BatchData
{
    public int BatchingKey { get; set; }
    public VertexArrayObject Vao { get; set; } = default!;
    
    public void MergeVertexBuffer(Buffer<float> buffer)
    {
        var newBuffer = new float[Vao.VertexBuffer!.Data.Length + buffer.Data.Length];
        Array.Copy(Vao.VertexBuffer.Data, newBuffer, Vao.VertexBuffer.Data.Length);
        Array.Copy(buffer.Data, 0, newBuffer, Vao.VertexBuffer.Data.Length, buffer.Data.Length);
        Vao.VertexBuffer!.Dispose();
        Vao.VertexBuffer = new Buffer<float>(newBuffer, buffer.VertexDeclaration);
    }
}
