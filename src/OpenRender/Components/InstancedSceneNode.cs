using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.InteropServices;

namespace OpenRender.SceneManagement;

public class InstancedSceneNode<TInstanceData> : SceneNode where TInstanceData : struct
{
    private readonly int vbInstanceData;
    private readonly List<TInstanceData> instanceDataList = new();
    private TInstanceData[] instanceData = Array.Empty<TInstanceData>();

    public InstancedSceneNode(Mesh mesh, Material? material = default) : base(mesh, material)
    {
        RenderGroup = RenderGroup.Default;

        mesh.VertexBuffer.SetName("InstancedSceneNode_Buffer1");

        var attributeIndexStart = 4;
        var bufferSlot = 1;    //  buffer slot for instance data
        GL.CreateBuffers(1, out vbInstanceData);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbInstanceData, -1, "InstancedSceneNode_Buffer2");
        GL.VertexArrayVertexBuffer(mesh.VertexBuffer.Vao, bufferSlot, vbInstanceData, 0, Marshal.SizeOf<Matrix4>());
        GL.VertexArrayBindingDivisor(mesh.VertexBuffer.Vao, bufferSlot, 1);
        for (var i = 0; i < 4; i++)
        {
            GL.VertexArrayAttribFormat(mesh.VertexBuffer.Vao, attributeIndexStart + i, 4, VertexAttribType.Float, false, i * sizeof(float) * 4);
            GL.VertexArrayAttribBinding(mesh.VertexBuffer.Vao, attributeIndexStart + i, bufferSlot);
            GL.EnableVertexArrayAttrib(mesh.VertexBuffer.Vao, attributeIndexStart + i);
        }
        Log.CheckGlError();

        var shader = new Shader("Shaders/instanced.vert", "Shaders/standard.frag");
        material!.Shader = shader;
        DisableCulling = true;
    }

    public List<TInstanceData> InstanceDataList => instanceDataList;

    public void AddInstanceData(TInstanceData data) => instanceDataList.Add(data);

    public void UpdateInstanceData()
    {
        if (instanceData.Length > 0)    //  need to ensure that the buffer is created, which happens inside OnDraw if instanceData.length == 0
        {
            instanceData = instanceDataList.ToArray();
            GL.NamedBufferSubData(vbInstanceData, 0, instanceDataList.Count * Marshal.SizeOf<TInstanceData>(), instanceData);
            Log.CheckGlError();
        }
    }

    public override void OnDraw(Scene scene, double elapsed)
    {
        GL.BindVertexArray(Mesh.VertexBuffer.Vao);
        if (instanceData.Length == 0)
        {
            instanceData = instanceDataList.ToArray();
            GL.NamedBufferData(vbInstanceData, instanceDataList.Count * Marshal.SizeOf<TInstanceData>(), instanceData, BufferUsageHint.DynamicDraw);
            Log.CheckGlError();
        }
        if (Mesh.DrawMode == DrawMode.Indexed)
            GL.DrawElementsInstanced(PrimitiveType.Triangles, Mesh.VertexBuffer.Indices!.Length, DrawElementsType.UnsignedInt, 0, instanceDataList.Count);
        else
            GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, Mesh.VertexBuffer.Vertices.Length, instanceDataList.Count);
        Log.CheckGlError();
    }
}
