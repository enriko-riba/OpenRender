using OpenTK.Graphics.OpenGL4;

namespace OpenRender.Core.Rendering;

public interface IVertexBuffer
{
    //T[] Data { get; set; }
    int Length { get; }
    uint[]? Indices { get; set; }
    int Stride { get; }
    int Vao { get; }
    VertexDeclaration VertexDeclaration { get; }

    void SetLabel(string name);
}

public class VertexBuffer : IVertexBuffer
{
    private readonly int vao;
    private readonly int vbo;
    private readonly int ibo;
    private readonly VertexDeclaration vertexDeclaration;
    private float[] vertices;

    public VertexBuffer(VertexDeclaration vertexDeclaration, float[] vertices) : this(vertexDeclaration, vertices, null) { }

    public VertexBuffer(VertexDeclaration vertexDeclaration, float[] vertices, uint[]? indices)
    {
        this.vertexDeclaration = vertexDeclaration;
        this.vertices = vertices;
        Indices = indices;

        GL.CreateVertexArrays(1, out vao);
        if (indices != null)
        {
            GL.CreateBuffers(1, out ibo);
            GL.VertexArrayElementBuffer(vao, ibo);
            GL.NamedBufferData(ibo, indices.Length * sizeof(uint), indices, BufferUsageHint.DynamicDraw);
        }

        GL.CreateBuffers(1, out vbo);
        vertexDeclaration.Apply(vao);
        GL.VertexArrayVertexBuffer(vao, 0, vbo, 0, vertexDeclaration.Stride);
        GL.NamedBufferData(vbo, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);
    }
    

    /// <summary>
    /// Adds GL labels for debugging.
    /// </summary>
    /// <param name="name"></param>
    public void SetLabel(string name)
    {
        GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, vao, -1, $"VAO {name}");
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbo, -1, $"VB {name}");
        if (Indices != null)
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, ibo, -1, $"IB {name}");
    }
    /*
    /// <summary>
    /// Calculates the bitangent and tangent vectors based on vertex buffer vertices and inserts them into the data.
    /// Note: The buffer needs to contain at least vertices, UVs and normal otherwise an exception is thrown.
    ///       The vertex buffers layout and data is changed after this method completes.
    /// </summary>
    /// <param name="bitangentAttributeLocation"></param>
    /// <param name="tangentAttributeLocation"></param>
    public void CalculateAndInsertBtnData(
        int tangentAttributeLocation = (int)VertexAttribLocation.Tangent,
        int bitangentAttributeLocation = (int)VertexAttribLocation.Bitangent)
    {
        if (data is null || data.Length == 0)
            throw new InvalidOperationException("Data is null or empty!");

        var vertexAttribute = vertexDeclaration.GetAttribute(VertexAttribLocation.Position);
        var vertexOffsetInFloats = vertexAttribute.Offset / sizeof(float);
        var vertexSizeInFloats = vertexAttribute.Size;

        var uvAttribute = vertexDeclaration.GetAttribute(VertexAttribLocation.TextureCoord);
        var uvAttributeOffsetInFloats = uvAttribute.Offset / sizeof(float);

        var strideInFloats = vertexDeclaration.Stride / sizeof(float);
        var elementCount = data.Length / strideInFloats;

        //  add TB attributes to the vertex declaration
        var tangentAttributeLayout = new VertexAttribLayout("Tangent", tangentAttributeLocation, 3, VertexAttribType.Float, 0);
        var bitangentAttributeLayout = new VertexAttribLayout("Bitangent", bitangentAttributeLocation, 3, VertexAttribType.Float, 0);
        //vertexDeclaration.AddAttribute(tangentAttributeLayout);
        //vertexDeclaration.AddAttribute(bitangentAttributeLayout);
        var newStrideInFloats = vertexDeclaration.Stride / sizeof(float);

        //  create a new array to hold previous data and the added tangent and bitangent vectors
        var newData = new float[elementCount * newStrideInFloats];

        //  copy the previous data to the new array interleaving the tangent and bitangent vectors
        for (var elementIndex = 0; elementIndex < elementCount; elementIndex++)
        {
            //  take all bytes before the tangent and bitangent vectors
            data.AsSpan(elementIndex * strideInFloats, strideInFloats)
                .CopyTo(newData.AsSpan(elementIndex * newStrideInFloats, strideInFloats));

            //  calculate the tangent and bitangent vectors
            var tangent = Vector3.Zero;
            var bitangent = Vector3.Zero;

            //  copy tangent and bitangent to the new array
        }

        if (Indices != null)
        {
            for (var i = 0; i < Indices.Length-2; i++)
            {
                var v0Idx = Indices[i] * (uint)strideInFloats + (uint)vertexOffsetInFloats;
                var v1Idx = Indices[i + 1] * (uint)strideInFloats + (uint)vertexOffsetInFloats;
                var v2Idx = Indices[i + 2] * (uint)strideInFloats + (uint)vertexOffsetInFloats;
                CalculateBtn(v0Idx, v1Idx, v2Idx, uvAttributeOffsetInFloats);
            }
        }
        else
        {
            for (var i = 0; i < data.Length; i += strideInFloats)
            {
                var v0Idx = (uint)(i + vertexOffsetInFloats);
                var v1Idx = (uint)(i + vertexOffsetInFloats + strideInFloats);
                var v2Idx = (uint)(i + vertexOffsetInFloats + 2 * strideInFloats);
                CalculateBtn(v0Idx, v1Idx, v2Idx, uvAttributeOffsetInFloats);
            }
        }
    }

    private void CalculateBtn(uint v0idx, uint v1idx, uint v2idx, int uvOffset)
    {
        var v0 = new Vector3(data[v0idx], data[v0idx + 1], data[v0idx + 2]);
        var v1 = new Vector3(data[v1idx], data[v1idx + 1], data[v1idx + 2]);
        var v2 = new Vector3(data[v2idx], data[v2idx + 1], data[v2idx + 2]);
        var uv0 = new Vector2(data[v0idx + uvOffset], data[v0idx + uvOffset + 1]);
        var uv1 = new Vector2(data[v1idx + uvOffset], data[v1idx + uvOffset + 1]);
        var uv2 = new Vector2(data[v2idx + uvOffset], data[v2idx + uvOffset + 1]);

        // Edges of the triangle
        var deltaPos1 = v1 - v0;
        var deltaPos2 = v2 - v0;

        // UV delta
        var deltaUV1 = uv1 - uv0;
        var deltaUV2 = uv2 - uv0;

        var r = 1.0f / (deltaUV1.X * deltaUV2.Y - deltaUV1.Y * deltaUV2.X);
        var tangent = (deltaPos1 * deltaUV2.Y - deltaPos2 * deltaUV1.Y) * r;
        var bitangent = (deltaPos2 * deltaUV1.X - deltaPos1 * deltaUV2.X) * r;
    }
    */
    /// <summary>
    /// Gets or sets the vertex buffer data which are float values representing vertices and other vertex attributes.
    /// </summary>
    public float[] Data
    {
        get => vertices;
        set
        {
            vertices = value;
            GL.NamedBufferSubData(vbo, 0, vertices.Length * sizeof(float), vertices);   //  TODO: test if updating works
        }
    }
    public int Length => vertices.Length;

    public uint[]? Indices { get; set; }
    public int Vao => vao;
    public int Stride => vertexDeclaration.Stride;
    public VertexDeclaration VertexDeclaration => vertexDeclaration;
}
