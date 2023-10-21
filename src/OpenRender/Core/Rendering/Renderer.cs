using OpenRender.Core.Culling;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using System.Runtime.CompilerServices;

namespace OpenRender.Core.Rendering;

public class Renderer
{
    private readonly Dictionary<RenderGroup, LayerData> layerData = new();

    private struct LayerData
    {
        public uint commandsBuffer;
        public uint uniformsBuffer;
        public uint materialsBuffer;
        public VertexArrayObject vao;

        public List<DrawElementsIndirectCommand> Commands;
        public List<Vertex> Vertices;
        public List<uint> Indices;
    }

    public Renderer()
    {
        // Create the default render layers
        foreach (var renderGroup in Enum.GetValues<RenderGroup>())
        {
            var data = new LayerData
            {
                vao = new VertexArrayObject(),
                Vertices = new List<Vertex>(),
                Indices = new List<uint>(),
                Commands = new List<DrawElementsIndirectCommand>()
            };
            GL.GenBuffers(1, out data.commandsBuffer);
            GL.GenBuffers(1, out data.uniformsBuffer);
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
                //  TODO: implement flag for nodes that don't get batched and have their own Draw call
                //  if(node.FrameBits.HasFlag(FrameBitsFlags.NoBatch))
                //  {
                //      node.OnDraw(elapsedSeconds);
                //      continue;
                //  }

                WriteUniformData(node, data);
                WriteDrawCommand(node, data);
                //  TODO: think of SceneNode implementing WriteUniformData and WriteDrawCommand
                //  so those can be overridden for special implementations e.g. node.WriteUniformData()...
            }
            else
            {
                node.OnDraw(null, elapsedSeconds);
            }
        }
        //  TODO: invoke draw call
    }

    private void WriteUniformData(SceneNode node, LayerData data)
    {
        
    }

    private unsafe void WriteDrawCommand(SceneNode node, LayerData data)
    {
        //  TODO: we need rebuilding geometry only if a new node is added
        //  to the scene or if we implement culling and the nodes get visible or culled.
        //node.Mesh.GetGeometry(out var nodeVertices, out var nodeIndices);
        var nodeIndices = node.Mesh.Indices;
        var nodeVertices = Unsafe.As<Vertex[]>(node.Mesh.Vertices);
       
        data.Commands.Add(new DrawElementsIndirectCommand
        {
            Count = nodeIndices.Length,
            InstanceCount = 1,
            FirstIndex = data.Indices.Count,
            BaseVertex = data.Vertices.Count,
            BaseInstance = 0
        });

        data.Vertices.AddRange(nodeVertices);
        data.Indices.AddRange(nodeIndices);
    }
}
