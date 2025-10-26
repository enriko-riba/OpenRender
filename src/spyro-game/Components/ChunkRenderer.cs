using OpenRender;
using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.World;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace SpyroGame.Components;

public class ChunkRenderer : SceneNode
{
    /// <summary>
    /// Default number <cref>BlockState</cref> instance structure for which SSBO buffer memory is allocated.
    /// </summary>
    private static readonly int DefaultMaxInstances = (VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize) / 2;

    private readonly uint texturesSSBO;
    private readonly uint materialsSSBO;
    private readonly List<Chunk> loadedChunksList = [];
    private readonly VoxelWorld world;
    private readonly ulong foamHandle;
    private readonly Shader waterShader;
    private readonly VertexArrayObject waterVao;
    private double uTime;

    internal ConcurrentQueue<Chunk> chunksStreamingQueue = [];

    public static ChunkRenderer Create(VoxelWorld world, ulong[] textureHandles, VoxelMaterial[] materials)
    {
        var (vertices, indices) = VoxelHelper.CreateVoxelBox();
        var shader = new Shader("Shaders/instancedChunk.vert", "Shaders/instancedChunk.frag");
        var dummyMaterial = Material.Default;    //  TODO: implement CreateNew() or CreateDefault() in Material
        dummyMaterial.Shader = shader;
        var mesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
        return new ChunkRenderer(world, mesh, dummyMaterial, textureHandles, materials);
    }

    public ChunkRenderer(VoxelWorld world, Mesh mesh, Material material, ulong[] textureHandles, VoxelMaterial[] materials) : base(mesh, material)
    {
        this.world = world;

        var foam = Texture.FromFile(["resources/foam.png"]);
        var sampler = Sampler.Create(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.Repeat, TextureWrapMode.Repeat);
        foamHandle = foam.GetBindlessHandle(sampler);
        textureHandles = [.. textureHandles, foamHandle];
        foreach (var handle in textureHandles)
        {
            if (handle != 0) Texture.MakeResident(handle);
        }

        waterShader = new Shader("Shaders/water.vert", "Shaders/water.frag");

        //  prepare textures and materials SSBOs
        GL.CreateBuffers(1, out texturesSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, texturesSSBO, -1, "voxelTextures_SSBO");
        GL.NamedBufferStorage(texturesSSBO, textureHandles.Length * Unsafe.SizeOf<ulong>(), textureHandles, BufferStorageFlags.MapWriteBit /*| BufferStorageFlags.DynamicStorageBit*/);
        GL.CreateBuffers(1, out materialsSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, materialsSSBO, -1, "voxelMaterials_SSBO");
        GL.NamedBufferStorage(materialsSSBO, materials.Length * Unsafe.SizeOf<VoxelMaterial>(), materials, BufferStorageFlags.MapWriteBit /*| BufferStorageFlags.DynamicStorageBit*/);
        Log.CheckGlError();

        waterVao = new VertexArrayObject();
        waterVao.AddBuffer(VertexDeclarations.VertexPositionTexture, [
            -1f, 0,  1,  0, 1,   // left near
             1f, 0, -1,  1, 0,   // right far
            -1f, 0, -1,  0, 0,   // left far
             1f, 0,  1,  1, 1    // right near
        ], "eboWaterPlane");
        waterVao.AddIndexBuffer(new IndexBuffer([0, 1, 2, 0, 3, 1]), "iboWaterPlane");

        RenderGroup = RenderGroup.SkyBox;
        DisableCulling = true;
    }


    /// <summary>
    /// Total number of blocks rendered.
    /// </summary>
    public int RenderedBlocks { get; private set; }

    public int ChunksQueueLength => chunksStreamingQueue.Count;

    public int ChunkRenderDataLength => loadedChunksList.Count;

    public IEnumerable<Chunk> VisibleChunks => loadedChunksList.Where(x => x.Visible);

    public BlockState? PickedBlock { get; set; }

    public override void OnDraw(double elapsed)
    {
        uTime += elapsed;
        GL.BindVertexArray(Vao!);

        /*
         * Z pre-pass
         * 
         * Note: it turns we are not fill rate bound therefore this is not used anymore, but keeping it here for reference.
         * 
        shaderZpass.Use();
        shaderZpass.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        GL.DepthMask(true);                         // enable depth buffer writes
        GL.DepthFunc(DepthFunction.Less);           // use normal depth order testing
        GL.Enable(EnableCap.DepthTest);             // and we want to perform depth tests
        foreach (var chunkData in sortedVisibleChunkList)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkData.BlocksSSBO);
            transform.worldMatrix.Row3.Xyz = chunkData.Position;
            shaderZpass.SetMatrix4("model", ref transform.worldMatrix);
            GL.DrawElementsInstanced(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0, chunkData.SolidCount);
        }
        GL.DepthMask(false);
        GL.DepthFunc(DepthFunction.Lequal);
        */

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, texturesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, materialsSSBO);

        RenderedBlocks = 0;
        foreach (var chunkData in loadedChunksList)
        {
            if (chunkData.Visible && chunkData.SolidCount > 0)
            {
                RenderedBlocks += chunkData.SolidCount;
                if (PickedBlock != null && VoxelHelper.GetChunkIndexFromPositionGlobal(PickedBlock.Value.GlobalPosition) == chunkData.Index)
                {
                    Material.Shader.SetInt("outlinedBlockId", PickedBlock.Value.Index);
                }
                else
                {
                    Material.Shader.SetInt("outlinedBlockId", -1);
                }

                _ = AreChunkSSBOsRecreated(chunkData);
                RenderChunk(chunkData.Position, chunkData.BlocksSSBO, chunkData.SolidCount);
            }
        }
        //GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);

        RenderWater();
        //foreach (var chunkData in loadedChunksList)
        //{
        //    if (chunkData.Visible && chunkData.TransparentCount > 0)
        //    {
        //        RenderedBlocks += chunkData.TransparentCount;
        //        RenderChunk(chunkData.Position, chunkData.TransparentBlocksSSBO, chunkData.TransparentCount, true);
        //    }
        //}

    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        var isChunkDataUpdated = ProcessChunksStreamingQueue();

        if (isChunkDataUpdated)
        {
            //  TODO: not sure why this happens sometimes, investigate. The bellow is a 
            //  workaround to remove chunks that are not in the surrounding chunks list
            foreach (var loadedChunk in loadedChunksList)
            {
                if (!world.SurroundingChunkIndices.Contains(loadedChunk.Index))
                {
                    loadedChunksList.Remove(loadedChunk);
                    break;  //  only first one, otherwise collection modified exception will throw
                }
            }
        }
    }

    /// <summary>
    /// Dequeues work items from chunks queue and either adds, removes or update chunk render data.
    /// </summary>
    /// <returns></returns>
    private bool ProcessChunksStreamingQueue()
    {
        const int MaxQueueItems = 4;    //  limit the number of processed queue items per frame

        //  update chunk render data, needs to be done on the main thread so we limit the number of chunks to be updated per frame
        var isChunkDataUpdated = false;
        var counter = 0;
        while (counter < MaxQueueItems && chunksStreamingQueue.TryDequeue(out var chunk))
        {
            loadedChunksList.Remove(chunk);

            switch (chunk.State)
            {
                case ChunkState.ToBeRemoved:
                    GL.DeleteBuffer(chunk.BlocksSSBO);
                    GL.DeleteBuffer(chunk.TransparentBlocksSSBO);
                    chunk.State = ChunkState.SafeToRemove;
                    isChunkDataUpdated = true;
                    break;

                case ChunkState.Loaded:
                    CreateChunkRenderData(chunk);
                    chunk.State = ChunkState.Added;
                    loadedChunksList.Add(chunk);
                    isChunkDataUpdated = true;
                    counter++;
                    break;

                case ChunkState.Added:
                    UpdateChunkRenderData(chunk);
                    loadedChunksList.Add(chunk);
                    isChunkDataUpdated = true;
                    counter++;
                    break;

                case ChunkState.SafeToRemove:
                    Log.Debug($"chunk {chunk.Index} safe to remove");
                    break;

                default:
                    break;
            }
        }
        return isChunkDataUpdated;
    }

    /// <summary>
    /// Immediately adds a chunk to the renderer. This method is intended to be used only during initialization.
    /// Note: this method must be called from the main thread (where GL context is created).
    /// </summary>
    /// <param name="chunk"></param>
    internal void AddChunkDirect(Chunk chunk)
    {
        CreateChunkRenderData(chunk);
        loadedChunksList.Add(chunk);
    }

    /// <summary>
    /// Renders one chunk
    /// </summary>
    /// <param name="position"></param>
    /// <param name="ssbo"></param>
    /// <param name="instanceCount"></param>
    /// <param name="isWater"></param>
    private void RenderChunk(Vector3i position, uint ssbo, int instanceCount, bool isWater = false)
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, ssbo);

        transform.worldMatrix.Row3.Xyz = position;
        var shader = isWater ? waterShader : Material.Shader;
        shader.SetMatrix4("model", ref transform.worldMatrix);
        shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        if (shader.UniformExists("uTime")) shader.SetFloat("uTime", (float)uTime * 0.2f);

        GL.Enable(EnableCap.CullFace);
        if (!ShowBoundingSphere)
        {
            GL.DrawElementsInstanced(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, IntPtr.Zero, instanceCount);
        }
        else
        {
            //GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
            //GL.DrawElementsInstanced(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0, instanceCount);

            //GL.Disable(EnableCap.CullFace);
            Matrix4.CreateScale(VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, VoxelHelper.ChunkSideSize, out var scaleMatrix);
            var worldMatrix = scaleMatrix * transform.worldMatrix;
            Scene!.DefaultShader.Use();
            Scene.DefaultShader.SetMatrix4("model", ref worldMatrix);
            GL.DrawElements(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0);        
        }
    }

    private void RenderWater()
    {
        GL.BindVertexArray(waterVao);
        waterShader.Use();
        GL.Disable(EnableCap.CullFace);

        // Enable blending before drawing water
        //GL.Enable(EnableCap.Blend);
        //GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        //  note: above is enabled by default in OpenRenderer

        Matrix4.CreateScale(VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ, 1, VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ, out var scaleMatrix);
        var worldMatrix = scaleMatrix * transform.worldMatrix;
        worldMatrix.Row3.Xyz = new Vector3(0, VoxelHelper.WaterLevel + 0.85f, 0);
        waterShader.SetMatrix4("model", ref worldMatrix);
        waterShader.SetFloat("uTime", (float)uTime * 0.2f);
        GL.DrawElements(PrimitiveType.Triangles, waterVao.DataLength, DrawElementsType.UnsignedInt, 0);
        
        // Disable blending after drawing water so it doesn't affect other objects
        //GL.Disable(EnableCap.Blend);
    }

    /// <summary>
    /// Verifies if the GL SSBOs are valid, when invalid new buffers are allocated.
    /// </summary>
    /// <param name="chunk"></param>
    /// <returns>true if new buffers where allocated</returns>
    private static bool AreChunkSSBOsRecreated(Chunk chunk)
    {
        //  TODO: investigate why on very rare occasions the SSBO is reported as invalid, the bellow is a workaround
        if (!GL.IsBuffer(chunk.BlocksSSBO) || !GL.IsBuffer(chunk.TransparentBlocksSSBO))
        {
            GL.DeleteBuffer(chunk.BlocksSSBO);
            GL.DeleteBuffer(chunk.TransparentBlocksSSBO);
            CreateChunkRenderData(chunk);
            return true;
        }
        return false;
    }

    private static void CreateChunkRenderData(Chunk chunk)
    {
        //  prepare block data SSBOs
        //  for solid blocks
        var blockData = chunk.VisibleBlocks.Select(x => new GpuBlockState(x.Index, (byte)x.FrontDirection, (byte)x.BlockType)).ToArray();
        var maxInstances = DefaultMaxInstances > blockData.Length ? DefaultMaxInstances : blockData.Length;
        GL.CreateBuffers(1, out uint blocksSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, blocksSSBO, -1, $"blocks_Chunk_{chunk}_SSBO");
        GL.NamedBufferStorage(blocksSSBO, maxInstances * Unsafe.SizeOf<GpuBlockState>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.NamedBufferSubData(blocksSSBO, 0, blockData.Length * Unsafe.SizeOf<GpuBlockState>(), blockData);

        //  prepare block data SSBOs
        //  for transparent blocks
        var transparentBlockData = chunk.TransparentBlocks.Select(x => new GpuBlockState(x.Index, (byte)x.FrontDirection, (byte)x.BlockType)).ToArray();
        maxInstances = DefaultMaxInstances > transparentBlockData.Length ? DefaultMaxInstances : transparentBlockData.Length;
        GL.CreateBuffers(1, out uint transparentBlocksSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, transparentBlocksSSBO, -1, $"transparentBlocks_Chunk_{chunk}_SSBO");
        GL.NamedBufferStorage(transparentBlocksSSBO, maxInstances * Unsafe.SizeOf<GpuBlockState>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.NamedBufferSubData(transparentBlocksSSBO, 0, transparentBlockData.Length * Unsafe.SizeOf<GpuBlockState>(), transparentBlockData);
        Log.CheckGlError();

        chunk.SolidCount = blockData.Length;
        chunk.TransparentCount = transparentBlockData.Length;
        chunk.BlocksSSBO = blocksSSBO;
        chunk.TransparentBlocksSSBO = transparentBlocksSSBO;
    }

    private static void UpdateChunkRenderData(Chunk chunk)
    {
        if (AreChunkSSBOsRecreated(chunk)) return;

        var blocksSSBO = chunk.BlocksSSBO;
        var transparentBlocksSSBO = chunk.TransparentBlocksSSBO;
        var blockData = chunk.VisibleBlocks.Select(x => new GpuBlockState(x.Index, (byte)x.FrontDirection, (byte)x.BlockType)).ToArray();
        var maxInstances = DefaultMaxInstances > blockData.Length ? DefaultMaxInstances : blockData.Length;
        //  has the allocated gpu buffer enough space for the new block data?
        if (maxInstances > chunk.SolidCount && maxInstances > DefaultMaxInstances)
        {
            GL.NamedBufferStorage(blocksSSBO, maxInstances * Unsafe.SizeOf<GpuBlockState>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        }
        GL.NamedBufferSubData(blocksSSBO, 0, blockData.Length * Unsafe.SizeOf<GpuBlockState>(), blockData);

        var transparentBlockData = chunk.TransparentBlocks.Select(x => new GpuBlockState(x.Index, (byte)x.FrontDirection, (byte)x.BlockType)).ToArray();
        maxInstances = DefaultMaxInstances > transparentBlockData.Length ? DefaultMaxInstances : transparentBlockData.Length;
        //  has the allocated gpu buffer enough space for the new block data?
        if (maxInstances > chunk.TransparentCount && maxInstances > DefaultMaxInstances)
        {
            GL.NamedBufferStorage(transparentBlocksSSBO, maxInstances * Unsafe.SizeOf<GpuBlockState>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        }
        GL.NamedBufferSubData(transparentBlocksSSBO, 0, transparentBlockData.Length * Unsafe.SizeOf<GpuBlockState>(), transparentBlockData);
        chunk.SolidCount = blockData.Length;
        chunk.TransparentCount = transparentBlockData.Length;
        chunk.BlocksSSBO = blocksSSBO;
        chunk.TransparentBlocksSSBO = transparentBlocksSSBO;
    }

    private record struct GpuBlockState(int Index, byte FrontDirection, byte BlockType);
}