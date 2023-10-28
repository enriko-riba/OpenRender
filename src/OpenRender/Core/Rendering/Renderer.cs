using OpenRender.Core.Culling;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;

namespace OpenRender.Core.Rendering;

public class Renderer
{
    private const int MaxCommands = 10000;

    /// <summary>
    /// Key: shader.handle + vertex declaration
    /// </summary>
    private readonly Dictionary<string, BatchData> batchDataDictionary = new();

    public void PrepareBatching(IEnumerable<SceneNode> renderList)
    {
        var nodes = renderList.Where(n => n.FrameBits.HasFlag(FrameBitsFlags.BatchAllowed));
        var shaderFrequencies = CalculateShaderFrequency(nodes);
        var batchFrequency = new Dictionary<string, int>();

        foreach (var node in nodes)
        {
            var frequency = shaderFrequencies[node.Material.Shader.Handle];
            if (frequency > 1)
            {
                var key = GetBatchingKey(node);
                if (batchFrequency.ContainsKey(key))
                    batchFrequency[key]++;
                else
                    batchFrequency[key] = 1;
            }
        }

        //  create and update individual batches
        foreach (var node in nodes)
        {
            var key = GetBatchingKey(node);
            if (batchFrequency.ContainsKey(key) && batchFrequency[key] > 1)
            {
                if (!batchDataDictionary.ContainsKey(key))
                {
                    AddNewBatch(batchDataDictionary, key, node.Material.Shader, node.Mesh.VertexDeclaration);
                }
                var data = batchDataDictionary[key];
                data.WriteDrawCommand(node);
                data.WriteTransformData(node);
            }
        }

        //  finish individual batches
        var batches = batchDataDictionary.Values;
        foreach (var batch in batches)
        {
            var buffer = new Buffer<float>(batch.Vertices.ToArray(), BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
            batch.Vao.AddBuffer(batch.VertexDeclaration, buffer, name: "Batch VBO");
            batch.Vao.AddIndexBuffer(new IndexBuffer(batch.Indices.ToArray()), "Batch IBO");
            batch.Vertices.Clear();
            batch.Indices.Clear();

            var commands = batch.CommandsDict.Values.ToArray();
            var transforms = batch.TransformsDict.Values.ToArray();
            GL.NamedBufferSubData(batch.CommandsBufferName, IntPtr.Zero, commands.Length * Unsafe.SizeOf<DrawElementsIndirectCommand>(), commands);
            GL.NamedBufferSubData(batch.TransformsBufferName, IntPtr.Zero, transforms.Length * Unsafe.SizeOf<Matrix4>(), transforms);
        }
    }

    public void RenderLayer(Scene scene, IEnumerable<SceneNode> renderList, double elapsedSeconds)
    {
        var lastShaderProgram = 0;        
        foreach(var batch in batchDataDictionary.Values)
        {
            //batch.CommandsDict.Clear();
            //batch.TransformsDict.Clear();
        }

        foreach (var node in renderList)
        {
            var shouldRender = (node.FrameBits.Value & (uint)FrameBitsFlags.RenderMask) == 0;
            var key = GetBatchingKey(node);
            var batchExists = batchDataDictionary.TryGetValue(key, out var batch);
            if (batchExists)
            {
                batch.UpdateCommand(node, shouldRender);
                batch.WriteTransformData(node);
            }

            if (shouldRender && !batchExists)
            {
                scene.RenderNode(node, elapsedSeconds);
            }
        }


        //  render all batches
        foreach (var batch in batchDataDictionary.Values)
        {
            //if (batch.FrameCommands.Count == 0) continue;
            if (lastShaderProgram != batch.Shader.Handle)
            {
                lastShaderProgram = batch.Shader.Handle;
                batch.Shader.Use();
                if (scene.vboCamera.IsUniformSupported(batch.Shader)) scene.vboCamera.BindToShaderProgram(batch.Shader);
                if (scene.vboLight.IsUniformSupported(batch.Shader)) scene.vboLight.BindToShaderProgram(batch.Shader);
                if (scene.vboMaterial.IsUniformSupported(batch.Shader)) scene.vboMaterial.BindToShaderProgram(batch.Shader);
            }
            var commands = batch.CommandsDict.Values.ToArray();
            var transforms = batch.TransformsDict.Values.ToArray();
            GL.NamedBufferSubData(batch.CommandsBufferName, IntPtr.Zero, commands.Length * Unsafe.SizeOf<DrawElementsIndirectCommand>(), commands);
            GL.NamedBufferSubData(batch.TransformsBufferName, IntPtr.Zero, transforms.Length * Unsafe.SizeOf<Matrix4>(), transforms);
            //GL.NamedBufferSubData(batch.Vao.VertexBuffer!.Vbo, IntPtr.Zero, batch.Vertices.Count * sizeof(float), batch.Vertices.ToArray());
            GL.BindVertexArray(batch.Vao);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, batch.TransformsBufferName);
            GL.BindBuffer(BufferTarget.DrawIndirectBuffer, batch.CommandsBufferName);
            GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, IntPtr.Zero, commands.Length, 0);
            Log.CheckGlError();
        }
    }

    /// <summary>
    /// Returns a batching key consisting of shader program handle and vertex declaration.
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    private static string GetBatchingKey(SceneNode node)
    {
        var locations = string.Join('|', node.Mesh.VertexDeclaration.Attributes.Select(x => $"{x.Location}:{x.Size}"));
        var key = $"{node.Material.Shader.Handle}|{locations}";
        return key;
    }

    private static void AddNewBatch(IDictionary<string, BatchData> dict, string key, Shader shader, VertexDeclaration vertexDeclaration)
    {
        var data = new BatchData
        {
            CommandsDict = new Dictionary<uint, DrawElementsIndirectCommand>(),
            TransformsDict = new Dictionary<uint, Matrix4>(),
            Shader = shader,
            VertexDeclaration = vertexDeclaration,
            Vertices = new List<float>(),
            Indices = new List<uint>(),
            Vao = new()
        };
        GL.CreateBuffers(1, out data.CommandsBufferName);
        GL.CreateBuffers(1, out data.TransformsBufferName);
        GL.CreateBuffers(1, out data.MaterialsBufferName);

        //  prepare draw commands
        GL.NamedBufferStorage(data.CommandsBufferName, MaxCommands * Unsafe.SizeOf<DrawElementsIndirectCommand>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);

        //  prepare matrices
        GL.NamedBufferStorage(data.TransformsBufferName, MaxCommands * Unsafe.SizeOf<Matrix4>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        Log.CheckGlError();

        dict.Add(key, data);
    }

    /// <summary>
    /// Calculates the cardinality of shader programs in the given scene node collection.
    /// </summary>
    /// <param name="nodes"></param>
    /// <returns></returns>
    private static Dictionary<int, int> CalculateShaderFrequency(IEnumerable<SceneNode> nodes)
    {
        var frequencies = new Dictionary<int, int>();
        foreach (var node in nodes)
        {
            var handle = node.Material?.Shader.Handle ?? 0;
            if (handle > 0)
            {
                if (frequencies.ContainsKey(handle))
                {
                    frequencies[handle]++;
                }
                else
                {
                    frequencies[handle] = 1;
                }
            }
        }
        return frequencies;
    }

    /// <summary>
    /// Holds batch related data like commands, SSBO buffers, batch geometry.
    /// </summary>
    private struct BatchData
    {
        public uint CommandsBufferName;
        public uint TransformsBufferName;
        public uint MaterialsBufferName;

        /// <summary>
        /// Holds draw commands of all batched nodes. Key is node ID.
        /// </summary>
        public Dictionary<uint, DrawElementsIndirectCommand> CommandsDict;
        
        /// <summary>
        /// Holds node transforms of all batched nodes. Key is node ID.
        /// </summary>
        public Dictionary<uint, Matrix4> TransformsDict;

        public List<float> Vertices;
        public List<uint> Indices;
        //public List<Transform> Transforms;
        public VertexArrayObject Vao;
        public VertexDeclaration VertexDeclaration;
        public Shader Shader;

        public readonly void WriteTransformData(SceneNode node)
        {
            node.GetTransform(out var transform);
            TransformsDict[node.Id] = transform.worldMatrix;
        }

        public readonly void WriteDrawCommand(SceneNode node)
        {
            var nodeIndices = node.Mesh.Indices;
            var cmd = new DrawElementsIndirectCommand
            {
                Count = nodeIndices.Length,
                InstanceCount = 0,
                FirstIndex = Indices.Count,
                BaseVertex = Vertices.Count / node.Mesh.VertexDeclaration.StrideInFloats,
                BaseInstance = 0
            };
            CommandsDict.Add(node.Id, cmd);
            Vertices.AddRange(node.Mesh.Vertices);
            Indices.AddRange(nodeIndices);
        }       

        public readonly void UpdateCommand(SceneNode node, bool shouldRender)
        {
            var cmd = CommandsDict[node.Id];
            cmd.InstanceCount = shouldRender ? 1 : 0;
            CommandsDict[node.Id] = cmd;
        }
    }
}
