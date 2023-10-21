using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace OpenRender.Core.Rendering;

public interface IVertex
{
    ReadOnlySpan<float> Data { get; }
    static VertexDeclaration VertexDeclaration { get; } = default!;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vertex : IVertex
{
    public Vertex(Vector3 position, Vector3 normal, Vector2 uv)
    {
        Position = position;
        Normal = normal;
        TexCoords = uv;
    }

    public Vertex(float x, float y, float z, float nx, float ny, float nz, float u, float v)
    {
        Position = new Vector3(x, y, z);
        Normal = new Vector3(nx, ny, nz);
        TexCoords = new Vector2(u, v);
    }

    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoords;

    public ReadOnlySpan<float> Data => MemoryMarshal.CreateReadOnlySpan(ref Position.X, 8);

    public static VertexDeclaration VertexDeclaration => VertexDeclarations.VertexPositionNormalTexture;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vertex2D : IVertex
{
    public Vertex2D(Vector2 position, Vector2 uv)
    {
        Position = position;
        TexCoords = uv;
    }

    public Vertex2D(float x, float y, float u, float v)
    {
        Position = new Vector2(x, y);
        TexCoords = new Vector2(u, v);
    }

    public Vector2 Position;
    public Vector2 TexCoords;

    public ReadOnlySpan<float> Data => MemoryMarshal.CreateReadOnlySpan(ref Position.X, 4);

    public static VertexDeclaration VertexDeclaration => VertexDeclarations.VertexPosition2DTexture;
}