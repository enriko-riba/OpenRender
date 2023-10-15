using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OpenRender.Components;

/// <summary>
/// A scene node consisting of multiple geometry batched into a single draw call.
/// Note: this implementation supports only indexed draw calls, also all batched
/// data must have the same vertex declaration and use the same material.
/// </summary>
public class BatchedNode : SceneNode
{
    private readonly List<Vertex> vertices = new();
    private readonly List<uint> indices = new();
    private readonly List<Transform> transforms = new();

    private readonly uint indirectCommandsBuffer;
    private readonly uint modelMatrixBuffer;
    private readonly List<DrawElementsIndirectCommand> commands = new();

    private readonly VertexDeclaration vertexDeclaration;
    private readonly VertexArrayObject vao;

    private int count;

    public BatchedNode(VertexDeclaration vertexDeclaration, Material material)
    {
        this.vertexDeclaration = vertexDeclaration;
        var shader = new Shader("Shaders/standard_ssbo.vert", "Shaders/standard.frag");
        material.Shader = shader;
        Material = material;
        DisableCulling = true;
        vao = new VertexArrayObject();
        GL.GenBuffers(1, out indirectCommandsBuffer);
        GL.GenBuffers(1, out modelMatrixBuffer);
    }

    public void AddVertices(Vertex[] nodeVertices, uint[] nodeIndices, in Vector3 position, in Vector3? scale, in Vector3? eulerRot)
        => AddVertices(nodeVertices, nodeIndices, position, scale ?? Vector3.One, eulerRot ?? Vector3.Zero);

    public void AddVertices(Vertex[] nodeVertices, uint[] nodeIndices, in Vector3 position, in Vector3 scale, in Vector3 eulerRot)
    {
        //  calc only vertex elements and ignore other floats
        //var elements = vertices.Count / vertexDeclaration.StrideInFloats;
        commands.Add(new DrawElementsIndirectCommand
        {
            Count = nodeIndices.Length,
            InstanceCount = 1,
            FirstIndex = indices.Count,
            BaseVertex = vertices.Count,
            BaseInstance = 0
        });

        Quaternion.FromEulerAngles(eulerRot, out var rotation);
        var transform = new Transform
        {
            Rotation = rotation,
            Scale = scale,
            Position = position
        };
        transform.UpdateMatrix();
        transforms.Add(transform);

        vertices.AddRange(nodeVertices);
        indices.AddRange(nodeIndices);
        count++;
    }

    public void AddVertices(Vertex[] nodeVertices)
    {
        vertices.AddRange(nodeVertices);
    }

    public void BuildMesh()
    {
        var mesh = new Mesh(vao);
        vao.AddVertexBuffer(new VertexBuffer(vertices.ToArray()));
        vao.AddIndexBuffer(new IndexBuffer(indices.ToArray()));

        SetMesh(mesh);
        vertices.Clear();
        indices.Clear();

        //  upload draw commands
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, indirectCommandsBuffer);
        GL.BufferStorage(BufferTarget.DrawIndirectBuffer, commands.Count * Unsafe.SizeOf<DrawElementsIndirectCommand>(), commands.ToArray(), BufferStorageFlags.MapWriteBit);

        //  upload matrices params
        var matrices = transforms.Select(t => t.worldMatrix).ToArray();
        GL.BindBuffer(BufferTarget.ParameterBuffer, modelMatrixBuffer);
        GL.BufferStorage(BufferTarget.ParameterBuffer, matrices.Length * Unsafe.SizeOf<Matrix4>(), matrices, BufferStorageFlags.MapWriteBit);
        Log.CheckGlError();
    }

    public unsafe override void OnDraw(Scene scene, double elapsed)
    {
        GL.BindVertexArray(vao);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, modelMatrixBuffer);
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, indirectCommandsBuffer);
        GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, IntPtr.Zero, count, 0);
    }

    override protected void UpdateMatrix()
    {
        transform.worldMatrix = Matrix4.Identity;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct DrawElementsIndirectCommand
    {
        [FieldOffset(0)]
        public int Count;
        [FieldOffset(4)]
        public int InstanceCount;
        [FieldOffset(8)]
        public int FirstIndex;
        [FieldOffset(12)]
        public int BaseVertex;
        [FieldOffset(16)]
        public int BaseInstance;
    }
}
