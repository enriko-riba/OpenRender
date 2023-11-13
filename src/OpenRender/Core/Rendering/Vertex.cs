using OpenRender.Core.Buffers;
using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

public interface IVertexData
{
    float[] Data { get; }
}

public readonly struct VertexPosition
{
    public VertexPosition(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }
    
    public VertexPosition(ReadOnlySpan<float> data)
    {
        X = data[0];
        Y = data[1];
        Z = data[2];
    }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }
}

public readonly struct Vertex : IVertexData
{
    private readonly float[] dataArray = new float[8];

    public Vertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        dataArray[0] = position.X;
        dataArray[1] = position.Y;
        dataArray[2] = position.Z;
        dataArray[3] = normal.X;
        dataArray[4] = normal.Y;
        dataArray[5] = normal.Z;
        dataArray[6] = uv.X;
        dataArray[7] = uv.Y;
    }

    public Vertex(float x, float y, float z, float nx, float ny, float nz, float u, float v)
        : this(new(x, y, z), new(nx, ny, nz), new(u, v)) { }

    public readonly float[] Data => dataArray;
    public static VertexDeclaration VertexDeclaration => VertexDeclarations.VertexPositionNormalTexture;
}

public readonly struct Vertex2D : IVertexData
{
    private readonly float[] dataArray = new float[4];

    public Vertex2D(Vector2 position, Vector2 uv)
    {
        dataArray[0] = position.X;
        dataArray[1] = position.Y;
        dataArray[2] = uv.X;
        dataArray[3] = uv.Y;
    }

    public Vertex2D(float x, float y, float u, float v) : this(new(x, y), new(u, v)) { }

    public readonly float[] Data => dataArray;

    public static VertexDeclaration VertexDeclaration => VertexDeclarations.VertexPosition2DTexture;
}