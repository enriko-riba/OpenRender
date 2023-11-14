using OpenRender.Core.Buffers;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering.Batching;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using System.Runtime.CompilerServices;

namespace OpenRender.Core.Rendering;

public class Renderer
{
    private const int MaxCommands = 5000;

    private int lastProgramHandle;
    private uint lastMaterial;
    private bool hasNodeListChanged;
    private bool hasCameraChanged;

    /// <summary>
    /// Key: shader.handle + vertex declaration
    /// </summary>
    private readonly Dictionary<string, BatchData> batchDataDictionary = new();

    private readonly Frustum frustum = new();
    protected readonly Dictionary<RenderGroup, List<SceneNode>> renderLayers = new();

    protected internal readonly UniformBlockBuffer<CameraUniform> uboCamera;
    protected internal readonly UniformBlockBuffer<LightUniform> uboLight;
    protected internal readonly UniformBlockBuffer<MaterialUniform> uboMaterial;
    protected internal readonly UniformBlockBuffer<ResidentTextureData> uboTextures;

    public Renderer()
    {
        uboCamera = new UniformBlockBuffer<CameraUniform>("camera", 0);
        uboLight = new UniformBlockBuffer<LightUniform>("light", 1);
        uboMaterial = new UniformBlockBuffer<MaterialUniform>("material", 2);
        uboTextures = new UniformBlockBuffer<ResidentTextureData>("textures", 3);

        // 16 is minimum per OpenGL standard
        GL.GetInteger(GetPName.MaxTextureImageUnits, out var textureUnitsCount);
        Log.Info($"MaxTextureImageUnits: {textureUnitsCount}");

        // Create the default render layers
        foreach (var renderGroup in Enum.GetValues<RenderGroup>())
        {
            renderLayers.Add(renderGroup, new List<SceneNode>());
        }
    }

    public void ResetMaterial() => lastMaterial = 0;


    /// <summary>
    /// Renders visible nodes in the list.
    /// </summary>
    /// <param name="list"></param>
    /// <param name="elapsedSeconds"></param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RenderNodeList(IEnumerable<SceneNode> list, double elapsedSeconds)
    {
        foreach (var node in list)
        {
            if ((node.FrameBits.Value & (uint)FrameBitsFlags.RenderMask) == 0)
            {
                RenderNode(node, elapsedSeconds);
            }
        }
    }

    private void RenderNode(SceneNode node, double elapsed)
    {
        var material = node.Material;
        var shader = material.Shader;
        shader.Use();
        if (lastProgramHandle != shader.Handle)
        {
            lastProgramHandle = shader.Handle;
            if (uboCamera.IsUniformBlockSupported(shader)) uboCamera.BindToShaderProgram(shader);
            if (uboLight.IsUniformBlockSupported(shader)) uboLight.BindToShaderProgram(shader);
            if (uboMaterial.IsUniformBlockSupported(shader)) uboMaterial.BindToShaderProgram(shader);
            if (uboTextures.IsUniformBlockSupported(shader)) uboTextures.BindToShaderProgram(shader);
        }

        if (shader.UniformExists("model"))
        {
            node.GetWorldMatrix(out var worldMatrix);
            shader.SetMatrix4("model", ref worldMatrix);
        }

        if (lastMaterial != material.Id)
        {
            lastMaterial = material.Id;
            var settings = new MaterialUniform()
            {
                Diffuse = material.DiffuseColor,
                Emissive = material.EmissiveColor,
                Specular = material.SpecularColor,
                Shininess = material.Shininess,
                DetailTextureScaleFactor = material.DetailTextureScaleFactor,
                DetailTextureBlendFactor = material.DetailTextureBlendFactor,
            };
            uboMaterial.UpdateSettings(ref settings);
            
            //  TODO: the bindless texture needs to be resident in order to be used, do we need a explicit check for that?
            ResidentTextureData textureData = new()
            { 
                Diffuse = material.BindlessTextureHandles[0],
                Detail = material.BindlessTextureHandles[1],
                Normal = material.BindlessTextureHandles[2],
                Specular  = material.BindlessTextureHandles[3],
                Bump = material.BindlessTextureHandles[4],
                T6 = material.BindlessTextureHandles[5],
                T7 = material.BindlessTextureHandles[6],
                T8 = material.BindlessTextureHandles[7]
            };
            uboTextures.UpdateSettings(ref textureData);                        
        }
        node.OnDraw(elapsed);
    }

    internal void AddNode(SceneNode node)
    {
        renderLayers[node.RenderGroup].Add(node);
        hasNodeListChanged = true;
    }

    internal void RemoveNode(SceneNode node)
    {
        renderLayers[node.RenderGroup].Remove(node);
        hasNodeListChanged = true;
    }

    internal void RemoveAllNodes()
    {
        foreach (var layer in renderLayers.Values)
        {
            layer.Clear();
        }
        hasNodeListChanged = true;
    }

    public void PrepareBatching()
    {
        var nodes = renderLayers[RenderGroup.Default].Where(n => n.FrameBits.HasFlag(FrameBitsFlags.BatchAllowed));
        var shaderFrequencies = CalculateShaderFrequency(nodes);
        var batchFrequency = new Dictionary<string, int>();

        foreach (var node in nodes)
        {
            node.BatchingKey = GetBatchingKey(node);
            var frequency = shaderFrequencies[node.Material.Shader.Handle];
            if (frequency > 1)
            {
                if (batchFrequency.ContainsKey(node.BatchingKey))
                    batchFrequency[node.BatchingKey]++;
                else
                    batchFrequency[node.BatchingKey] = 1;
            }
        }

        //  create and update individual batches
        foreach (var node in nodes)
        {
            if (batchFrequency.TryGetValue(node.BatchingKey, out var frequency) && frequency > 1)
            {
                if (!batchDataDictionary.TryGetValue(node.BatchingKey, out var batch))
                {
                    batch = CreateNewBatch(node.Material.Shader, node.Mesh.VertexDeclaration, nodes.Count());
                    batchDataDictionary[node.BatchingKey] = batch;
                }
                batch.WriteDrawCommand(node);
            }
        }

        //  finish individual batches
        var batches = batchDataDictionary.Values;
        foreach (var batch in batches)
        {
            var buffer = new Buffer<float>(batch.Vertices.ToArray(), BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
            batch.Vao.AddBuffer(batch.VertexDeclaration, buffer, name: "Batch VBO");
            batch.Vao.AddIndexBuffer(new IndexBuffer(batch.Indices.ToArray()), "Batch IBO");
            //  TODO: what if vao contains multiple buffers, e.g. tangent/bitangent?

            batch.Vertices.Clear();
            batch.Indices.Clear();
        }
    }

    public void BeforeRenderFrame(ICamera camera, IReadOnlyList<LightUniform> lights)
    {
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        //  set shared uniform blocks for all programs before render loop
        var cam = new CameraUniform()
        {
            view = camera.ViewMatrix,
            projection = camera.ProjectionMatrix,
            position = camera.Position,
            direction = camera.Front
        };
        uboCamera.UpdateSettings(ref cam);

        if (lights.Any())
        {
            //  TODO: pass lights array
            var dirLight = lights[0];
            uboLight.UpdateSettings(ref dirLight);
        }
    }

    public void RenderFrame(double elapsedSeconds)
    {
        //  render each layer separately
        var renderList = renderLayers[RenderGroup.SkyBox];
        RenderNodeList(renderList, elapsedSeconds);

        RenderDefaultLayer(elapsedSeconds);

        renderList = renderLayers[RenderGroup.DistanceSorted];
        RenderNodeList(renderList, elapsedSeconds);

        renderList = renderLayers[RenderGroup.UI];
        RenderNodeList(renderList, elapsedSeconds);
    }

    public void RenderDefaultLayer(double elapsedSeconds)
    {
        var lastShaderProgram = 0;
        var renderList = renderLayers[RenderGroup.Default];

        foreach (var node in renderList)
        {
            var shouldRender = (node.FrameBits.Value & (uint)FrameBitsFlags.RenderMask) == 0;

            var batchExists = batchDataDictionary.TryGetValue(node.BatchingKey, out var batch);
            if (batchExists)
            {
                batch!.UpdateCommand(node, shouldRender);
            }

            if (shouldRender && !batchExists)
            {
                RenderNode(node, elapsedSeconds);
            }
        }

        //  render all batches
        foreach (var batch in batchDataDictionary.Values)
        {
            if (lastShaderProgram != batch.Shader.Handle)
            {
                lastShaderProgram = batch.Shader.Handle;
                batch.Shader.Use();
                if (uboCamera.IsUniformBlockSupported(batch.Shader)) uboCamera.BindToShaderProgram(batch.Shader);
                if (uboLight.IsUniformBlockSupported(batch.Shader)) uboLight.BindToShaderProgram(batch.Shader);
                if (uboMaterial.IsUniformBlockSupported(batch.Shader)) uboMaterial.BindToShaderProgram(batch.Shader);
            }

            var length = batch.LastIndex + 1;
            GL.NamedBufferSubData(batch.CommandsBufferName, IntPtr.Zero, length * Unsafe.SizeOf<DrawElementsIndirectCommand>(), batch.CommandsDataArray);
            GL.NamedBufferSubData(batch.WorldMatricesBufferName, IntPtr.Zero, length * Unsafe.SizeOf<Matrix4>(), batch.WorldMatricesDataArray);
            GL.NamedBufferSubData(batch.MaterialsBufferName, IntPtr.Zero, length * Unsafe.SizeOf<MaterialData>(), batch.MaterialDataArray);
            GL.NamedBufferSubData(batch.TexturesBufferName, IntPtr.Zero, length * Unsafe.SizeOf<ResidentTextureData>(), batch.TextureDataArray);

            GL.BindVertexArray(batch.Vao);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, batch.WorldMatricesBufferName);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, batch.MaterialsBufferName);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, batch.TexturesBufferName);
            GL.BindBuffer(BufferTarget.DrawIndirectBuffer, batch.CommandsBufferName);
            GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, IntPtr.Zero, length, 0);
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

    private static BatchData CreateNewBatch( Shader shader, VertexDeclaration vertexDeclaration, int maxBatchSize)
    {
        var data = new BatchData(shader, vertexDeclaration, maxBatchSize);

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
        GL.NamedBufferStorage(data.TexturesBufferName, MaxCommands * Unsafe.SizeOf<ResidentTextureData>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);

        Log.CheckGlError();

        return data;
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
            var handle = node.Material.Shader.Handle;
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

    internal void Update(ICamera? camera, IEnumerable<SceneNode> nodes)
    {
        CullFrustum(camera, nodes);
        SortRenderList(camera);
    }


    private void CullFrustum(ICamera? camera, IEnumerable<SceneNode> nodes)
    {
        hasCameraChanged = (camera?.Update() ?? false);
        if (hasCameraChanged)
        {
            frustum.Update(camera!);
            CullingHelper.FrustumCull(frustum, nodes);
        }
    }

    private void SortRenderList(ICamera? camera)
    {
        if (hasCameraChanged || hasNodeListChanged)
        {
            var distanceSortedLayer = renderLayers[RenderGroup.DistanceSorted];
            if (distanceSortedLayer.Any(n => (n.FrameBits.Value & (uint)FrameBitsFlags.RenderMask) == 0))
            {
                distanceSortedLayer.Sort(new DistanceComparer(camera!.Position));
            }

            var defaultLayer = renderLayers[RenderGroup.Default];
            if (defaultLayer.Any(n => (n.FrameBits.Value & (uint)FrameBitsFlags.RenderMask) == 0))
            {
                defaultLayer.Sort((a, b) => a.Material.Shader.Handle - b.Material.Shader.Handle);
            }
        }
    }
}
