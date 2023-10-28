using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;
using static System.Runtime.InteropServices.JavaScript.JSType;

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

    private readonly uint commandsBuffer;
    private readonly uint modelMatrixBuffer;
    private readonly uint materialsBuffer;

    private readonly List<DrawElementsIndirectCommand> commands = new();
    private readonly VertexDeclaration vertexDeclaration;

    public BatchedNode(VertexDeclaration vertexDeclaration, Material material) : base(null, material)
    {
        var shader = new Shader("Shaders/standard_ssbo.vert", "Shaders/standard.frag");
        material.Shader = shader;
        Material = material;
        DisableCulling = true;

        GL.CreateBuffers(1, out commandsBuffer);
        GL.CreateBuffers(1, out modelMatrixBuffer);
        GL.CreateBuffers(1, out materialsBuffer);
        this.vertexDeclaration = vertexDeclaration;
    }

    public void AddVertices(Vertex[] nodeVertices, uint[] nodeIndices, in Vector3 position, in Vector3? scale, in Vector3? eulerRot)
        => AddVertices(nodeVertices, nodeIndices, position, scale ?? Vector3.One, eulerRot ?? Vector3.Zero);

    public void AddVertices(Vertex[] nodeVertices, uint[] nodeIndices, in Vector3 position, in Vector3 scale, in Vector3 eulerRot)
    {
        //  calc only vertex elements and ignore other floats
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
    }

    public void BuildMesh()
    {
        var mesh = new Mesh(vertexDeclaration, vertices.ToArray(), indices.ToArray());
        SetMesh(mesh);
        vertices.Clear();
        indices.Clear();

        //  upload draw commands
        GL.NamedBufferStorage(commandsBuffer, commands.Count * Unsafe.SizeOf<DrawElementsIndirectCommand>(), commands.ToArray(), BufferStorageFlags.DynamicStorageBit);

        //  upload matrices params
        var matrices = transforms.Select(t => t.worldMatrix).ToArray();
        GL.NamedBufferStorage(modelMatrixBuffer, matrices.Length * Unsafe.SizeOf<Matrix4>(), matrices, BufferStorageFlags.DynamicStorageBit);
        Log.CheckGlError();
    }

    public unsafe override void OnDraw(double elapsed)
    {
        GL.BindVertexArray(Mesh.Vao!);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, modelMatrixBuffer);
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, commandsBuffer);
        GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, IntPtr.Zero, commands.Count, 0);
    }

    override protected void UpdateMatrix()
    {
        transform.worldMatrix = Matrix4.Identity;
    }
}
