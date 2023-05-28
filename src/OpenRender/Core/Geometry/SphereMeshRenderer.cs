using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;

namespace OpenRender.Core.Geometry;

public class SphereMeshRenderer
{
    public static readonly SphereMeshRenderer DefaultSphereMeshRenderer = new();
    private readonly VertexBuffer vb;
    private readonly Shader shader;

    public SphereMeshRenderer()
    {
        ScaleMatrix = Matrix4.CreateScale(1.15f);
        vb = GeometryHelper.CreateSphere(12, 18);
        shader = new Shader("Shaders/spheremesh.vert", "Shaders/spheremesh.frag");
    }

    public Matrix4 ScaleMatrix { get; set; }
    public Shader Shader => shader;

    public void Render()
    {
        shader.Use();
        GL.BindVertexArray(vb.Vao);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
        GL.DrawElements(PrimitiveType.Triangles, vb.Indices!.Length, DrawElementsType.UnsignedInt, 0);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
        GL.BindVertexArray(0);
    }
}