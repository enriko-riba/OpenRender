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
    private static readonly int DefaultMaxInstances = (VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize) / 2;

    private readonly uint texturesSSBO;
    private readonly uint materialsSSBO;
    private readonly List<Chunk> loadedChunksList = [];
    private readonly VoxelWorld world;
    private double uTime;

    internal ConcurrentQueue<Chunk> chunksStreamingQueue = [];

    public static ChunkRenderer Create(VoxelWorld world, ulong[] textureHandles, VoxelMaterial[] materials)
    {
        var (vertices, indices) = VoxelHelper.CreateVoxelBox();
        var shader = new Shader("Shaders/instancedChunk.vert", "Shaders/instancedChunk.frag");
        var dummyMaterial = Material.Default;
        dummyMaterial.Shader = shader;
        var mesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
        return new ChunkRenderer(world, mesh, dummyMaterial, textureHandles, materials);
    }

    public ChunkRenderer(VoxelWorld world, Mesh mesh, Material material, ulong[] textureHandles, VoxelMaterial[] materials) : base(mesh, material)
    {
        this.world = world;

        foreach (var handle in textureHandles)
        {
            if (handle != 0) Texture.MakeResident(handle);
        }

        //  prepare textures and materials SSBOs
        GL.CreateBuffers(1, out texturesSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, texturesSSBO, -1, "voxelTextures_SSBO");
        GL.NamedBufferStorage(texturesSSBO, textureHandles.Length * Unsafe.SizeOf<ulong>(), textureHandles, BufferStorageFlags.MapWriteBit);
        GL.CreateBuffers(1, out materialsSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, materialsSSBO, -1, "voxelMaterials_SSBO");
        GL.NamedBufferStorage(materialsSSBO, materials.Length * Unsafe.SizeOf<VoxelMaterial>(), materials, BufferStorageFlags.MapWriteBit);
        Log.CheckGlError();

        RenderGroup = RenderGroup.Default; // Changed from SkyBox to Solid
        DisableCulling = true;
    }


    public int RenderedBlocks { get; private set; }

    public int ChunksQueueLength => chunksStreamingQueue.Count;

    public int ChunkRenderDataLength => loadedChunksList.Count;

    public IEnumerable<Chunk> VisibleChunks => loadedChunksList.Where(x => x.Visible);

    public BlockState? PickedBlock { get; set; }

    public override void OnDraw(double elapsed)
    {
        uTime += elapsed;
        GL.BindVertexArray(Vao!);

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
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        var isChunkDataUpdated = ProcessChunksStreamingQueue();

        if (isChunkDataUpdated)
        {
            foreach (var loadedChunk in loadedChunksList)
            {
                if (!world.SurroundingChunkIndices.Contains(loadedChunk.Index))
                {
                    loadedChunksList.Remove(loadedChunk);
                    break;  
                }
            }
        }
    }

    private bool ProcessChunksStreamingQueue()
    {
        const int MaxQueueItems = 4;
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

    internal void AddChunkDirect(Chunk chunk)
    {
        CreateChunkRenderData(chunk);
        loadedChunksList.Add(chunk);
    }

    private void RenderChunk(Vector3i position, uint ssbo, int instanceCount)
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, ssbo);

        transform.worldMatrix.Row3.Xyz = position;
        Material.Shader.SetMatrix4("model", ref transform.worldMatrix);
        Material.Shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        if (Material.Shader.UniformExists("uTime")) Material.Shader.SetFloat("uTime", (float)uTime * 0.2f);

        GL.Enable(EnableCap.CullFace);
        if (!ShowBoundingSphere)
        {
            GL.DrawElementsInstanced(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, IntPtr.Zero, instanceCount);
        }
        else
        {
            Matrix4.CreateScale(VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, VoxelHelper.ChunkSideSize, out var scaleMatrix);
            var worldMatrix = scaleMatrix * transform.worldMatrix;
            Scene!.DefaultShader.Use();
            Scene.DefaultShader.SetMatrix4("model", ref worldMatrix);
            GL.DrawElements(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0);
        }
    }

    private static bool AreChunkSSBOsRecreated(Chunk chunk)
    {
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
        var blockData = chunk.VisibleBlocks.Select(x => new GpuBlockState(x.Index, (byte)x.FrontDirection, (byte)x.BlockType)).ToArray();
        var maxInstances = DefaultMaxInstances > blockData.Length ? DefaultMaxInstances : blockData.Length;
        GL.CreateBuffers(1, out uint blocksSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, blocksSSBO, -1, $"blocks_Chunk_{chunk}_SSBO");
        GL.NamedBufferStorage(blocksSSBO, maxInstances * Unsafe.SizeOf<GpuBlockState>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.NamedBufferSubData(blocksSSBO, 0, blockData.Length * Unsafe.SizeOf<GpuBlockState>(), blockData);

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
        if (maxInstances > chunk.SolidCount && maxInstances > DefaultMaxInstances)
        {
            GL.NamedBufferStorage(blocksSSBO, maxInstances * Unsafe.SizeOf<GpuBlockState>(), IntPtr.Zero, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        }
        GL.NamedBufferSubData(blocksSSBO, 0, blockData.Length * Unsafe.SizeOf<GpuBlockState>(), blockData);

        var transparentBlockData = chunk.TransparentBlocks.Select(x => new GpuBlockState(x.Index, (byte)x.FrontDirection, (byte)x.BlockType)).ToArray();
        maxInstances = DefaultMaxInstances > transparentBlockData.Length ? DefaultMaxInstances : transparentBlockData.Length;
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