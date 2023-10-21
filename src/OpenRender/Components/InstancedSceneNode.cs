using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;

namespace OpenRender.SceneManagement;

public class InstancedSceneNode<TInstanceData, TStateData> : SceneNode where TInstanceData : struct
{
    private readonly uint vbInstanceData;
    private readonly List<TInstanceData> instanceDataList = new();
    private readonly List<TStateData> stateDataList = new();
    private readonly TInstanceData[] instanceData;

    public InstancedSceneNode(Mesh mesh, int maxNumberOfInstances, Material? material = default) : base(mesh, material)
    {
        RenderGroup = RenderGroup.Default;
        instanceData = new TInstanceData[maxNumberOfInstances];

        var attributeIndexStart = (uint)VertexAttribLocation.ModelMatrix1;
        uint bufferSlot = 1;    //  buffer slot for instance data
        GL.CreateBuffers(1, out vbInstanceData);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, vbInstanceData, -1, "InstancedSceneNode_Buffer2");
        GL.VertexArrayVertexBuffer(mesh.Vao, bufferSlot, vbInstanceData, 0, Unsafe.SizeOf<Matrix4>());
        GL.VertexArrayBindingDivisor(mesh.Vao, bufferSlot, 1);
        for (uint i = 0; i < 4; i++)
        {
            GL.VertexArrayAttribFormat(mesh.Vao, attributeIndexStart + i, 4, VertexAttribType.Float, false, i * sizeof(float) * 4);
            GL.VertexArrayAttribBinding(mesh.Vao, attributeIndexStart + i, bufferSlot);
            GL.EnableVertexArrayAttrib(mesh.Vao, attributeIndexStart + i);
        }
        GL.NamedBufferStorage(vbInstanceData, maxNumberOfInstances * Unsafe.SizeOf<TInstanceData>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        Log.CheckGlError();

        var shader = new Shader("Shaders/instanced.vert", "Shaders/standard.frag");
        material!.Shader = shader;
        DisableCulling = true;
    }

    public TInstanceData[] InstanceData => instanceData;

    public IReadOnlyList<TStateData> StateDataList => stateDataList;

    public void AddInstanceData(in TInstanceData data, TStateData stateData)
    {
        instanceDataList.Add(data);
        stateDataList.Add(stateData);
    }

    public void UpdateInstanceData()
    {
        GL.NamedBufferSubData(vbInstanceData, 0, instanceDataList.Count * Unsafe.SizeOf<TInstanceData>(), instanceData);
        Log.CheckGlError();
    }

    public override void OnDraw(Scene scene, double elapsed)
    {
        GL.BindVertexArray(Mesh.Vao);
        if (Mesh.Vao.DrawMode == DrawMode.Indexed)
            GL.DrawElementsInstanced(PrimitiveType.Triangles, Mesh.Vao.DataLength, DrawElementsType.UnsignedInt, 0, instanceDataList.Count);
        else
            GL.DrawArraysInstanced(PrimitiveType.Triangles, 0, Mesh.Vao.DataLength, instanceDataList.Count);
        Log.CheckGlError();
    }
}
