using OpenRender.Core.Buffers;
using OpenRender.Core.Culling;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenTK.Mathematics;

namespace OpenRender.Core;

/// <summary>
/// Mesh is just a container for a geometry data.
/// </summary>
public class Mesh
{
    private readonly float[] vertices;
    private readonly uint[] indices;

    public VertexDeclaration VertexDeclaration { get; private set; }

    public Mesh(VertexDeclaration vertexDeclaration, Vertex[] vertices, uint[] indices) :
        this(vertexDeclaration, GetVertices(vertexDeclaration, vertices), indices) { }

    public Mesh(VertexDeclaration vertexDeclaration, Vertex2D[] vertices, uint[] indices)
    {
        VertexDeclaration = vertexDeclaration;
        this.indices = indices;
        this.vertices = GetVertices(vertexDeclaration, vertices);
        BoundingSphere = CullingHelper.CalculateBoundingSphere(VertexDeclaration.StrideInFloats, this.vertices);
    }

    public Mesh(VertexDeclaration vertexDeclaration, float[] vertices, uint[] indices)
    {
        VertexDeclaration = vertexDeclaration;
        this.vertices = vertices;
        this.indices = indices;
        BoundingSphere = CullingHelper.CalculateBoundingSphere(VertexDeclaration.StrideInFloats, vertices);
    }

    public BoundingSphere BoundingSphere;

    public uint[] Indices => indices;
    public float[] Vertices => vertices;

    public void CreateTBN()
    {
        // TBN requires normals
        if (!VertexDeclaration.Attributes.Any(x => x.Location == (uint)VertexAttribLocation.Normal))
        {
            throw new InvalidOperationException("TBN requires normals!");
        }

        vao ??= BuildVao();
        var uvOffset = VertexDeclaration.Attributes.First(x => x.Location == (uint)VertexAttribLocation.TextureCoord).Offset / sizeof(float);
        var stride = VertexDeclaration.StrideInFloats;        
        var vertexCount = vertices.Length / stride;
        var tbData = new float[vertexCount * 6];    //  3 floats tangent + 3 floats bitangent
        for (var i = 0; i < indices.Length; i += stride)
        {
            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];

            var v0 = vertices.AsSpan((int)i0 * stride, 3);
            var v1 = vertices.AsSpan((int)i1 * stride, 3);
            var v2 = vertices.AsSpan((int)i2 * stride, 3);

            var c0 = vertices.AsSpan((int)(i0 * stride + uvOffset), 2);
            var c1 = vertices.AsSpan((int)(i1 * stride + uvOffset), 2);
            var c2 = vertices.AsSpan((int)(i2 * stride + uvOffset), 2);

            var p0 = new Vector3(v0[0], v0[1], v0[2]);
            var p1 = new Vector3(v1[0], v1[1], v1[2]);
            var p2 = new Vector3(v2[0], v2[1], v2[2]);

            var uv0 = new Vector2(c0[0], c0[1]);
            var uv1 = new Vector2(c1[0], c1[1]);
            var uv2 = new Vector2(c2[0], c2[1]);

            var edge1 = p1 - p0;
            var edge2 = p2 - p0;

            var duv1 = uv1 - uv0;
            var duv2 = uv2 - uv0;

            var r = 1.0f / (duv1.X * duv2.Y - duv2.X * duv1.Y);

            var idx = (i / stride) * 6;
            tbData[idx + 0] = r * (duv2.Y * edge1.X - duv1.Y * edge2.X);
            tbData[idx + 1] = r * (duv2.Y * edge1.Y - duv1.Y * edge2.Y);
            tbData[idx + 2] = r * (duv2.Y * edge1.Z - duv1.Y * edge2.Z);
            tbData[idx + 3] = r * (-duv2.X * edge1.X + duv1.X * edge2.X);
            tbData[idx + 4] = r * (-duv2.X * edge1.Y + duv1.X * edge2.Y);
            tbData[idx + 5] = r * (-duv2.X * edge1.Z + duv1.X * edge2.Z);
        }
        var tbBuffer = new Buffer<float>(tbData);
        var tangentLayout = new VertexAttribLayout("tangent", 0, 3, OpenTK.Graphics.OpenGL4.VertexAttribType.Float, 0);
        var bitangentLayout = new VertexAttribLayout("bitangent", 1, 3, OpenTK.Graphics.OpenGL4.VertexAttribType.Float, 0);
        vao.AddBuffer(new VertexDeclaration(new VertexAttribLayout[] { tangentLayout, bitangentLayout }), tbBuffer, name: "TB");
    }
    
    private VertexArrayObject? vao;

    public VertexArrayObject BuildVao()
    {
        if (vertices != null)
        {
            vao = new VertexArrayObject();
            vao.AddBuffer(VertexDeclaration, new Buffer<float>(vertices));
            vao.AddIndexBuffer(new IndexBuffer(indices));
            return vao;
        }
        throw new ArgumentNullException(nameof(vertices));
    }

    public static float[] GetVertices<T>(VertexDeclaration vertexDeclaration, T[] vertices) where T : IVertexData
    {
        var length = vertices.Length * vertexDeclaration.StrideInFloats;
        var floats = new float[length];
        var offset = 0;
        foreach (var vertex in vertices)
        {
            var destination = floats.AsSpan(offset);
            vertex.Data.CopyTo(destination);
            offset += vertexDeclaration.StrideInFloats;
        }
        return floats;
    }
}
