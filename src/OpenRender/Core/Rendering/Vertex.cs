using OpenTK.Mathematics;

namespace OpenRender.Core.Rendering;

public struct Vertex
{
    public Vertex() { }

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
}
