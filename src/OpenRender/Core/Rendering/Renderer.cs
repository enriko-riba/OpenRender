using OpenRender.Core.Culling;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;

namespace OpenRender.Core.Rendering;

public class Renderer
{
    private readonly Dictionary<RenderGroup, LayerData> layerData = new();

    private struct LayerData
    {
        public uint commandsBuffer;
        public uint transformsBuffer;
        public uint materialsBuffer;
        public VertexArrayObject vao;

        public List<DrawElementsIndirectCommand> Commands;
        public List<float> Vertices;
        public List<uint> Indices;
        public List<Transform> Transforms;
        public VertexArrayObject Vao;
    }

    public Renderer()
    {
        // Create the default render layers
        foreach (var renderGroup in Enum.GetValues<RenderGroup>())
        {
            var data = new LayerData
            {
                vao = new VertexArrayObject(),
                Vertices = new List<float>(),
                Indices = new List<uint>(),
                Commands = new List<DrawElementsIndirectCommand>(),
                Transforms = new List<Transform>(),
                Vao = new()
            };
            GL.GenBuffers(1, out data.commandsBuffer);
            GL.GenBuffers(1, out data.transformsBuffer);
            GL.GenBuffers(1, out data.materialsBuffer);
            layerData.Add(renderGroup, data);
        }
    }

    public void Render(Dictionary<RenderGroup, List<SceneNode>> renderLayers, double elapsedSeconds)
    {
        //  render each layer separately
        var renderList = renderLayers[RenderGroup.SkyBox];
        var data = layerData[RenderGroup.SkyBox];
        RenderLayer(renderList, data, elapsedSeconds);

        renderList = renderLayers[RenderGroup.Default];
        data = layerData[RenderGroup.Default];
        RenderLayer(renderList, data, elapsedSeconds);

        renderList = renderLayers[RenderGroup.DistanceSorted];
        data = layerData[RenderGroup.DistanceSorted];
        RenderLayer(renderList, data, elapsedSeconds);

        renderList = renderLayers[RenderGroup.UI];
        data = layerData[RenderGroup.UI];
        RenderLayer(renderList, data, elapsedSeconds);
    }

    private void RenderLayer(IEnumerable<SceneNode> list, LayerData data, double elapsedSeconds)
    {
        foreach (var node in list)
        {
            if ((node.FrameBits.Value & (uint)FrameBitsFlags.RenderMask) == 0)
            {
                if (!node.FrameBits.HasFlag(FrameBitsFlags.BatchAllowed))
                {
                    node.OnDraw(elapsedSeconds);
                    continue;
                }

                WriteTransformData(node, data);
                WriteDrawCommand(node, data);
                //  TODO: think of SceneNode implementing WriteUniformData and WriteDrawCommand
                //  so those can be overridden for special implementations e.g. node.WriteUniformData()...
            }
        }

        //  TODO: optimize writing to buffer storage if no changes were made
        //  upload draw commands
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, data.commandsBuffer);
        GL.BufferStorage(BufferTarget.DrawIndirectBuffer, data.Commands.Count * Unsafe.SizeOf<DrawElementsIndirectCommand>(), data.Commands.ToArray(), BufferStorageFlags.MapWriteBit);

        //  upload matrices params
        GL.BindBuffer(BufferTarget.ParameterBuffer, data.transformsBuffer);
        GL.BufferStorage(BufferTarget.ParameterBuffer, data.Transforms.Count * Unsafe.SizeOf<Matrix4>(), data.Transforms.Select(t => t.worldMatrix).ToArray(), BufferStorageFlags.MapWriteBit);
        Log.CheckGlError();

        GL.BindVertexArray(data.Vao);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, data.transformsBuffer);
        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, data.commandsBuffer);
        GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, IntPtr.Zero, data.Commands.Count, 0);
    }

    private static void WriteTransformData(SceneNode node, LayerData data)
    {
        node.GetTransform(out var transform);
        data.Transforms.Add(transform);
    }

    private static unsafe void WriteDrawCommand(SceneNode node, LayerData data)
    {
        //  TODO: we need rebuilding geometry only if a new node is added to the
        //  scene or if we implement culling and the nodes get visible or culled.
        var nodeIndices = node.Mesh.Indices;

        data.Commands.Add(new DrawElementsIndirectCommand
        {
            Count = nodeIndices.Length,
            InstanceCount = 1,
            FirstIndex = data.Indices.Count,
            BaseVertex = data.Vertices.Count / node.Mesh.VertexDeclaration.StrideInFloats,
            BaseInstance = 0
        });

        data.Vertices.AddRange(node.Mesh.Vertices);
        data.Indices.AddRange(nodeIndices);
    }
}
