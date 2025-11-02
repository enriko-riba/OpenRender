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
    private static readonly int MaxBlocksPerChunk = VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize;
    private static readonly int DefaultMaxInstances = MaxBlocksPerChunk / 2;
    private const BufferStorageFlags BlockBufferFlags = BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit;

    private readonly uint texturesSSBO;
    private readonly uint materialsSSBO;
    // Chunks with GPU render data ready (SSBOs). Indexed by chunk index for O(1) upserts.
    private readonly Dictionary<int, Chunk> loadedChunksMap = new();
    private readonly VoxelWorld world;
    private double uTime;

    internal ConcurrentQueue<Chunk> chunksStreamingQueue = [];
    // Reused buffers to reduce per-frame allocations for queue draining
    private readonly List<Chunk> drainBuffer = new(512);
    private readonly Dictionary<int, Chunk> latestByIndex = new(512);
    private readonly List<(Chunk chunk, float distSq)> sortBuffer = new(512);

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

    public int ChunkRenderDataLength => loadedChunksMap.Count;

    public IEnumerable<Chunk> VisibleChunks => loadedChunksMap.Values.Where(x => x.Visible);

    public BlockState? PickedBlock { get; set; }

    public override void OnDraw(double elapsed)
    {
        uTime += elapsed;
        GL.BindVertexArray(Vao!);

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, texturesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, materialsSSBO);

        RenderedBlocks = 0;
        var modelMatrix = Matrix4.Identity;
        foreach (var chunkData in loadedChunksMap.Values)
        {
            var canDraw = chunkData.SolidCount > 0 && chunkData.State == ChunkState.Added && chunkData.Visible;

            if (canDraw)
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
                RenderChunk(chunkData.Position, chunkData.BlocksSSBO, chunkData.SolidCount, ref modelMatrix);
            }
        }
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        _ = ProcessChunksStreamingQueue();
    }

    private bool ProcessChunksStreamingQueue()
    {
        // Drain a bounded number of items, coalesce by chunk index (keep last),
        // sort by distance to camera, process a fixed number for stable frame time.
        var isChunkDataUpdated = false;
        drainBuffer.Clear();
        while (drainBuffer.Count < 512 && chunksStreamingQueue.TryDequeue(out var c))
        {
            drainBuffer.Add(c);
        }
        if (drainBuffer.Count == 0) return false;

        latestByIndex.Clear();
        foreach (var c in drainBuffer)
        {
            latestByIndex[c.Index] = c;
        }

        sortBuffer.Clear();
        var cameraPos = world.Camera.Position;
        foreach (var kv in latestByIndex)
        {
            var ch = kv.Value;
            var center = ((Vector3)(ch.Aabb.Min + ch.Aabb.Max)) * 0.5f;
            var distSq = (center - cameraPos).LengthSquared;
            sortBuffer.Add((ch, distSq));
        }
        sortBuffer.Sort(static (a, b) => a.distSq.CompareTo(b.distSq));

        const int MaxPerFrame = 256;
        var processed = 0;
        var iEntry = 0;
        for (; iEntry < sortBuffer.Count && processed < MaxPerFrame; iEntry++)
        {
            var chunk = sortBuffer[iEntry].chunk;

            switch (chunk.State)
            {
                case ChunkState.ToBeRemoved:
                    // Guard against stale removals for interior chunks.
                    // If this chunk is still within the active radius around the camera, treat this as stale and keep it.
                    {
                        var camPos = world.Camera.Position;
                        var camChunkX = (int)((camPos.X + 0.5f) / VoxelHelper.ChunkSideSize);
                        var camChunkZ = (int)((camPos.Z + 0.5f) / VoxelHelper.ChunkSideSize);
                        var manhattan = Math.Abs(chunk.ChunkPosition.X - camChunkX) + Math.Abs(chunk.ChunkPosition.Y - camChunkZ);
                        if (manhattan <= VoxelHelper.MaxDistanceInChunks + 1)
                        {
                            // Stale removal: restore state and ensure it's tracked
                            chunk.State = ChunkState.Added;
                            loadedChunksMap[chunk.Index] = chunk;
                            chunk.PendingUpload = false;
                            break;
                        }
                    }

                    // Remove if present, then free GL buffers
                    loadedChunksMap.Remove(chunk.Index);
                    GL.DeleteBuffer(chunk.BlocksSSBO);
                    GL.DeleteBuffer(chunk.TransparentBlocksSSBO);
                    chunk.BlocksSSBO = 0;
                    chunk.TransparentBlocksSSBO = 0;
                    chunk.SolidCount = 0;
                    chunk.SolidCapacity = 0;
                    chunk.TransparentCount = 0;
                    chunk.TransparentCapacity = 0;
                    chunk.State = ChunkState.SafeToRemove;
                    chunk.PendingUpload = false;
                    isChunkDataUpdated = true;
                    break;

                case ChunkState.Loaded:
                    CreateChunkRenderData(chunk);
                    chunk.State = ChunkState.Added;
                    loadedChunksMap[chunk.Index] = chunk;
                    chunk.PendingUpload = false;
                    isChunkDataUpdated = true;
                    processed++;
                    break;

                case ChunkState.Added:
                    UpdateChunkRenderData(chunk);
                    loadedChunksMap[chunk.Index] = chunk;
                    chunk.PendingUpload = false;
                    isChunkDataUpdated = true;
                    processed++;
                    break;

                case ChunkState.SafeToRemove:
                    // nothing
                    break;
            }
        }
        // Re-enqueue any unprocessed entries to avoid dropping work
        for (; iEntry < sortBuffer.Count; iEntry++)
        {
            chunksStreamingQueue.Enqueue(sortBuffer[iEntry].chunk);
        }
        return isChunkDataUpdated;
    }

    internal void AddChunkDirect(Chunk chunk)
    {
        CreateChunkRenderData(chunk);
        chunk.State = ChunkState.Added;
        chunk.Visible = true;
        loadedChunksMap[chunk.Index] = chunk;
        chunk.PendingUpload = false;
    }

    private void RenderChunk(Vector3i position, uint ssbo, int instanceCount, ref Matrix4 modelMatrix)
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, ssbo);
        modelMatrix.Row3.Xyz = position;
        Material.Shader.SetMatrix4("model", ref modelMatrix);
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
            var worldMatrix = scaleMatrix * modelMatrix;
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

    private static int NextCapacity(int currentCapacity, int required)
    {
        if (required <= currentCapacity) return currentCapacity;

        var capacity = currentCapacity > 0 ? currentCapacity : DefaultMaxInstances;
        if (capacity <= 0) capacity = DefaultMaxInstances;

        while (capacity < required && capacity < MaxBlocksPerChunk)
        {
            capacity = Math.Min(capacity * 2, MaxBlocksPerChunk);
        }

        return Math.Max(capacity, required);
    }

    private static uint AllocateBlockBuffer(string label, int capacity)
    {
        GL.CreateBuffers(1, out uint buffer);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, buffer, -1, label);
        var sizeInBytes = Math.Max(1, capacity) * Unsafe.SizeOf<GpuBlockState>();
        GL.NamedBufferStorage(buffer, sizeInBytes, IntPtr.Zero, BlockBufferFlags);
        return buffer;
    }

    private static GpuBlockState CreateGpuBlockState(BlockState block)
    {
        var packedBytes = PackBlockMetadata(block.FrontDirection, block.BlockType);
        return new GpuBlockState(block.Index, packedBytes, block.PackedAO);
    }

    private static uint PackBlockMetadata(BlockDirection direction, BlockType blockType)
    {
        return (((uint)blockType) & 0xFFu) << 8 | (((uint)direction) & 0xFFu);
    }

    private static void CreateChunkRenderData(Chunk chunk)
    {
        var blockData = chunk.VisibleBlocks.Select(CreateGpuBlockState).ToArray();
        var solidCapacity = NextCapacity(0, blockData.Length);
        var blocksSSBO = AllocateBlockBuffer($"blocks_Chunk_{chunk}_SSBO", solidCapacity);
        GL.NamedBufferSubData(blocksSSBO, 0, blockData.Length * Unsafe.SizeOf<GpuBlockState>(), blockData);

        var transparentBlockData = chunk.TransparentBlocks.Select(CreateGpuBlockState).ToArray();
        var transparentCapacity = NextCapacity(0, transparentBlockData.Length);
        var transparentBlocksSSBO = AllocateBlockBuffer($"transparentBlocks_Chunk_{chunk}_SSBO", transparentCapacity);
        GL.NamedBufferSubData(transparentBlocksSSBO, 0, transparentBlockData.Length * Unsafe.SizeOf<GpuBlockState>(), transparentBlockData);
        Log.CheckGlError();

        chunk.SolidCount = blockData.Length;
        chunk.SolidCapacity = solidCapacity;
        chunk.TransparentCount = transparentBlockData.Length;
        chunk.TransparentCapacity = transparentCapacity;
        chunk.BlocksSSBO = blocksSSBO;
        chunk.TransparentBlocksSSBO = transparentBlocksSSBO;
    }

    private static void UpdateChunkRenderData(Chunk chunk)
    {
        if (AreChunkSSBOsRecreated(chunk)) return;

        var blockData = chunk.VisibleBlocks.Select(CreateGpuBlockState).ToArray();
        var requiredSolidCapacity = NextCapacity(chunk.SolidCapacity, blockData.Length);
        if (requiredSolidCapacity != chunk.SolidCapacity)
        {
            GL.DeleteBuffer(chunk.BlocksSSBO);
            chunk.BlocksSSBO = AllocateBlockBuffer($"blocks_Chunk_{chunk}_SSBO", requiredSolidCapacity);
            chunk.SolidCapacity = requiredSolidCapacity;
        }
        GL.NamedBufferSubData(chunk.BlocksSSBO, 0, blockData.Length * Unsafe.SizeOf<GpuBlockState>(), blockData);
        var transparentBlockData = chunk.TransparentBlocks.Select(CreateGpuBlockState).ToArray();
        var requiredTransparentCapacity = NextCapacity(chunk.TransparentCapacity, transparentBlockData.Length);
        if (requiredTransparentCapacity != chunk.TransparentCapacity)
        {
            GL.DeleteBuffer(chunk.TransparentBlocksSSBO);
            chunk.TransparentBlocksSSBO = AllocateBlockBuffer($"transparentBlocks_Chunk_{chunk}_SSBO", requiredTransparentCapacity);
            chunk.TransparentCapacity = requiredTransparentCapacity;
        }
        GL.NamedBufferSubData(chunk.TransparentBlocksSSBO, 0, transparentBlockData.Length * Unsafe.SizeOf<GpuBlockState>(), transparentBlockData);
        Log.CheckGlError();
        chunk.SolidCount = blockData.Length;
        chunk.TransparentCount = transparentBlockData.Length;
    }

    private struct GpuBlockState
    {
        public int Index;
        public uint PackedBytes;
        public uint PackedAO;

        public GpuBlockState(int index, uint packedBytes, uint packedAO)
        {
            Index = index;
            PackedBytes = packedBytes;
            PackedAO = packedAO;
        }
    }
}

