using OpenRender.Core.Culling;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

            //var commands = batch.CommandsDict.Values.ToArray();
            //var transforms = batch.TransformsDict.Values.ToArray();
            //var materials = batch.Materials.ToArray();
            //var textures = batch.Textures.ToArray();
            //GL.NamedBufferSubData(batch.CommandsBufferName, IntPtr.Zero, commands.Length * Unsafe.SizeOf<DrawElementsIndirectCommand>(), commands);
            //GL.NamedBufferSubData(batch.WorldMatricesBufferName, IntPtr.Zero, transforms.Length * Unsafe.SizeOf<Matrix4>(), transforms);
            //GL.NamedBufferSubData(batch.MaterialsBufferName, IntPtr.Zero, materials.Length * Unsafe.SizeOf<MaterialData>(), materials);
        }
    }

    public void RenderLayer(Scene scene, IEnumerable<SceneNode> renderList, double elapsedSeconds)
    {
        var lastShaderProgram = 0;

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
            var materials = batch.Materials.ToArray();
            var textures = batch.Textures.ToArray();
            GL.NamedBufferSubData(batch.CommandsBufferName, IntPtr.Zero, commands.Length * Unsafe.SizeOf<DrawElementsIndirectCommand>(), commands);
            GL.NamedBufferSubData(batch.WorldMatricesBufferName, IntPtr.Zero, transforms.Length * Unsafe.SizeOf<Matrix4>(), transforms);
            GL.NamedBufferSubData(batch.MaterialsBufferName, IntPtr.Zero, materials.Length * Unsafe.SizeOf<MaterialData>(), materials);
            GL.NamedBufferSubData(batch.TexturesBufferName, IntPtr.Zero, textures.Length * Unsafe.SizeOf<TextureData>(), textures);

            GL.BindVertexArray(batch.Vao);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, batch.WorldMatricesBufferName);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, batch.MaterialsBufferName);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, batch.TexturesBufferName);
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
            CommandsDict = new(),
            TransformsDict = new(),
            Materials = new(),
            Textures = new(),
            Shader = shader,
            VertexDeclaration = vertexDeclaration,
            Vertices = new List<float>(),
            Indices = new List<uint>(),
            Vao = new()
        };
        GL.CreateBuffers(1, out data.CommandsBufferName);
        GL.CreateBuffers(1, out data.WorldMatricesBufferName);
        GL.CreateBuffers(1, out data.MaterialsBufferName);
        GL.CreateBuffers(1, out data.TexturesBufferName);

        //  prepare draw commands
        GL.NamedBufferStorage(data.CommandsBufferName, MaxCommands * Unsafe.SizeOf<DrawElementsIndirectCommand>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);

        //  prepare matrices
        GL.NamedBufferStorage(data.WorldMatricesBufferName, MaxCommands * Unsafe.SizeOf<Matrix4>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);

        //  prepare materials
        GL.NamedBufferStorage(data.MaterialsBufferName, MaxCommands * Unsafe.SizeOf<MaterialData>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);

        //  prepare textures
        GL.NamedBufferStorage(data.TexturesBufferName, MaxCommands * Unsafe.SizeOf<TextureData>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);

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
        public uint WorldMatricesBufferName;
        public uint MaterialsBufferName;
        public uint TexturesBufferName;

        /// <summary>
        /// Holds draw commands of all batched nodes. Key is node ID.
        /// </summary>
        public Dictionary<uint, DrawElementsIndirectCommand> CommandsDict;

        /// <summary>
        /// Holds model (or world) matrices of all batched nodes. Key is node ID.
        /// </summary>
        public Dictionary<uint, Matrix4> TransformsDict;

        /// <summary>
        /// Holds material data of all batched nodes. 
        /// </summary>
        public List<MaterialData> Materials;

        /// <summary>
        /// Holds bindless texture handles of all batched nodes. 
        /// </summary>
        public List<TextureData> Textures;

        public VertexArrayObject Vao;
        public VertexDeclaration VertexDeclaration;
        public Shader Shader;

        public List<float> Vertices;
        public List<uint> Indices;


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

            //  write material data, this is a but more complex as we need bindless textures
            var materialData = new MaterialData()
            {
                Diffuse = new Vector4(node.Material.DiffuseColor),
                Emissive = new Vector4(node.Material.EmissiveColor),
                Specular = new Vector4(node.Material.SpecularColor, node.Material.Shininess),
                Extra = new Vector4(node.Material.DetailTextureFactor, 0,0,0),
                //Shininess = node.Material.Shininess,
                //DetailTextureFactor = node.Material.DetailTextureFactor,
                //HasDiffuse = node.Material.HasDiffuse ? 1 : 0
            };
            Materials.Add(materialData);

            TextureData textureData = new();
            if (node.Material.HasDiffuse)
            {
                textureData.Diffuse = GL.Arb.GetTextureHandle(node.Material.Textures[0].Handle);
                if (!Textures.Any(t => t.Diffuse == textureData.Diffuse))
                    GL.Arb.MakeTextureHandleResident(textureData.Diffuse);
            }
            Textures.Add(textureData);

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

    //[StructLayout(LayoutKind.Explicit)]
    //private struct MaterialData
    //{
    //    [FieldOffset(0)]
    //    public Vector3 Diffuse;
    //    [FieldOffset(16)]
    //    public Vector3 Emissive;
    //    [FieldOffset(32)]
    //    public Vector3 Specular;
    //    [FieldOffset(44)]
    //    public float Shininess;
    //    [FieldOffset(48)]
    //    public float DetailTextureFactor;
    //}
    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialData
    {
        public Vector4 Diffuse;
        public Vector4 Emissive;
        public Vector4 Specular;
        public Vector4 Extra;
        //public float Shininess;
        //public float DetailTextureFactor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextureData
    {
        public long Diffuse;
        public long Detail;
        public long Normal;
    }
}
