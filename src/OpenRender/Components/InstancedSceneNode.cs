using OpenRender.Core;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace OpenRender.SceneManagement;

public class InstancedSceneNode : SceneNode
{
    private readonly List<Matrix4> worldMatrices = new();

    public InstancedSceneNode(Mesh mesh, Material? material = default) : base(mesh, material)
    {
        RenderGroup = RenderGroup.Default;
        //  prepare buffer for world matrices
        var attributeIndex = 4;
        GL.CreateBuffers(1, out int mbo);
        GL.VertexArrayVertexBuffer(mesh.VertexBuffer.Vao, 0, mbo, 0, sizeof(float) * 16);
        for (var i = 0; i < 4; i++)
        {
            GL.VertexArrayAttribFormat(mesh.VertexBuffer.Vao, attributeIndex + i, 4, VertexAttribType.Float, false, sizeof(float) * i * 4);
            GL.VertexArrayAttribBinding(mesh.VertexBuffer.Vao, attributeIndex + i, 0);
            GL.VertexArrayBindingDivisor(mesh.VertexBuffer.Vao, attributeIndex + i, 1);
            GL.EnableVertexArrayAttrib(mesh.VertexBuffer.Vao, attributeIndex + i);
        }
        Log.CheckGlError();
    }

    public List<Matrix4> WorldMatrices => worldMatrices;

    public void AddInstance(Vector3 position, Vector3 rotation, Vector3 scale) => worldMatrices.Add(Matrix4.CreateScale(scale) *
        Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(rotation)) *
        Matrix4.CreateTranslation(position));

    public override void OnDraw(Scene scene, double elapsed)
    {
        GL.BindVertexArray(Mesh.VertexBuffer.Vao);
        if (Mesh.DrawMode == DrawMode.Indexed)
            GL.DrawElementsInstanced(PrimitiveType.Triangles, Mesh.VertexBuffer.Indices!.Length, DrawElementsType.UnsignedInt, IntPtr.Zero, worldMatrices.Count);
        else
            GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, Mesh.VertexBuffer.Vertices.Length, worldMatrices.Count);
    }
}
