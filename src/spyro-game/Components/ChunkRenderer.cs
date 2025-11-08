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
using System.Runtime.InteropServices;

namespace SpyroGame.Components;

public class ChunkRenderer : SceneNode
{
    private static readonly int MaxBlocksPerChunk = VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize;
    private static readonly int DefaultMaxInstances = MaxBlocksPerChunk / 2;
    private const BufferStorageFlags BlockBufferFlags = BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit;
    private const BufferStorageFlags PersistentWriteCoherent = BufferStorageFlags.MapWriteBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit | BufferStorageFlags.DynamicStorageBit;

    private readonly uint texturesSSBO;
    private readonly uint materialsSSBO;
    private uint drawDataSSBO;
    private uint indirectCmdBuffer;
    
    private uint drawVisibleSSBO;       // binding=10 for compute frustum result
    private uint compactedIndicesSSBO;  // binding=11 copy of world.CompactedChunkIndices for compute
    private uint drawnTotalSSBO;        // binding=14 for sum of InstanceCount
    private IntPtr drawnTotalPtr;
    private IntPtr drawDataMap;
    private IntPtr indirectMap;
    
    private int drawDataMapSizeBytes;
    private int indirectMapSizeBytes;
    
    // Chunks with GPU render data ready (SSBOs). Indexed by chunk index for O(1) upserts.
    private readonly Dictionary<int, Chunk> loadedChunksMap = new();
    private readonly VoxelWorld world;
    private double uTime;
    private int lastCompactionVersion = -1;
    public bool UseGpuFrustumCulling { get; set; } = false;
    // Temporary: avoid modifying indirect InstanceCount until driver sync is nailed down
    public bool ApplyIndirectCulling { get; set; } = false;

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

        // DrawData SSBO (binding=5) and indirect command buffer (MDI), persistently mapped
        var initialDrawCapacity = 256;
        CreateOrResizeMappedBuffer(ref drawDataSSBO, "voxelDrawData_SSBO", initialDrawCapacity * Unsafe.SizeOf<SpyroGame.World.DrawDataGpu>(), out drawDataMap, out drawDataMapSizeBytes);
        CreateOrResizeMappedBuffer(ref indirectCmdBuffer, "voxelIndirectCmdBuffer", initialDrawCapacity * GlIndirectCmdSize, out indirectMap, out indirectMapSizeBytes);

        RenderGroup = RenderGroup.Default; // Changed from SkyBox to Solid
        DisableCulling = true;
        world.VoxelIndexCount = Vao!.DataLength;
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
        // If world prepared draw buffers, prefer them
        var ddHandle = world.PreparedDrawDataSSBO != 0 ? world.PreparedDrawDataSSBO : drawDataSSBO;
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, ddHandle);
        if (drawVisibleSSBO != 0)
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, drawVisibleSSBO);
        DrawMdi();
    }

   
    public override void OnUpdate(Scene scene, double elapsed)
    {
        // Swap any pending compaction before doing per-frame work to avoid mid-frame swaps
        try { world.TrySwapPendingCompaction(); } catch { }

        // Do not trigger compaction here; VoxelWorld manages publishes to avoid double submits.

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

        // Phase 1: Loaded/Add updates first
        foreach (var entry in sortBuffer)
        {
            if (processed >= MaxPerFrame) break;
            var chunk = entry.chunk;
            if (chunk.State == ChunkState.Loaded)
            {
                CreateChunkRenderData(chunk);
                chunk.State = ChunkState.Added;
                loadedChunksMap[chunk.Index] = chunk;
                chunk.PendingUpload = false;
                isChunkDataUpdated = true;
                processed++;
            }
            else if (chunk.State == ChunkState.Added)
            {
                UpdateChunkRenderData(chunk);
                loadedChunksMap[chunk.Index] = chunk;
                chunk.PendingUpload = false;
                isChunkDataUpdated = true;
                processed++;
            }
        }

        // Phase 2: removals with remaining budget
        foreach (var entry in sortBuffer)
        {
            if (processed >= MaxPerFrame) break;
            var chunk = entry.chunk;
            if (chunk.State != ChunkState.ToBeRemoved) continue;

            var camPos = world.Camera.Position;
            var camChunkX = (int)((camPos.X + 0.5f) / VoxelHelper.ChunkSideSize);
            var camChunkZ = (int)((camPos.Z + 0.5f) / VoxelHelper.ChunkSideSize);
            var manhattan = Math.Abs(chunk.ChunkPosition.X - camChunkX) + Math.Abs(chunk.ChunkPosition.Y - camChunkZ);
            if (manhattan <= VoxelHelper.MaxDistanceInChunks + 1)
            {
                chunk.State = ChunkState.Added;
                loadedChunksMap[chunk.Index] = chunk;
                chunk.PendingUpload = false;
                continue;
            }

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
            processed++;
        }

        // Re-enqueue any entries that may still need work
        foreach (var entry in sortBuffer)
        {
            if (entry.chunk.PendingUpload) chunksStreamingQueue.Enqueue(entry.chunk);
        }

        // If streaming queue is empty, ensure full coverage: assign slots for any missing visible chunks.
        // No fixed-atlas fallback; renderer relies on GPU compaction results
        return isChunkDataUpdated;
    }

    // Fixed-atlas fallback removed; renderer relies on GPU compaction atlas only.

    internal void AddChunkDirect(Chunk chunk)
    {
        CreateChunkRenderData(chunk);
        chunk.State = ChunkState.Added;
        chunk.Visible = true;
        loadedChunksMap[chunk.Index] = chunk;
        chunk.PendingUpload = false;
        // Fixed-atlas path removed; uploads are unused in GPU compaction mode
    }

    private void RenderChunk(Vector3i position, uint ssbo, int instanceCount, ref Matrix4 modelMatrix)
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, ssbo);
        // Legacy uniforms set for shader fallback; draw data is preferred
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

    private void CreateOrResizeMappedBuffer(ref uint handle, string label, int sizeBytes, out IntPtr mapPtr, out int mapSize)
    {
        if (handle != 0) GL.DeleteBuffer(handle);
        GL.CreateBuffers(1, out handle);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, handle, -1, label);
        GL.NamedBufferStorage(handle, sizeBytes, IntPtr.Zero, PersistentWriteCoherent);
        mapPtr = GL.MapNamedBufferRange(handle, IntPtr.Zero, sizeBytes, BufferAccessMask.MapWriteBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit);
        mapSize = sizeBytes;
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

    // Draw types defined in SpyroGame.World

    private const int GlIndirectCmdSize = 5 * sizeof(uint); // 20 bytes

    private void UpdateDrawDataForChunk(Chunk chunk, int slot, int outlinedLocalIndex)
    {
        var model = Matrix4.Identity;
        model.Row3.Xyz = chunk.Position;
        var dd = new DrawDataGpu
        {
            Model = model,
            ChunkPos = new Vector4(chunk.Position.X, chunk.Position.Y, chunk.Position.Z, 1f),
            BlocksBase = 0, // legacy per-chunk SSBO path
            ChunkSize = VoxelHelper.ChunkSideSize,
            OutlinedLocalIndex = outlinedLocalIndex,
            Enabled = 1
        };
        unsafe
        {
            var offset = slot * Unsafe.SizeOf<DrawDataGpu>();
            var dst = (byte*)drawDataMap + offset;
            System.Buffer.MemoryCopy(&dd, dst, Unsafe.SizeOf<DrawDataGpu>(), Unsafe.SizeOf<DrawDataGpu>());
        }
    }

    // --- MDI path ---
    private unsafe void DrawMdi()
    {
        // Preferred: draw using prepared MDI buffers authored by publisher
        try { world.TrySwapPendingCompaction(); } catch { }
        if (world.PreparedIndirectCmdBuffer != 0 && world.PreparedDrawDataSSBO != 0 && world.PreparedDrawCountPtr != IntPtr.Zero && world.CompactedAtlasSSBO != 0)
        {
            var drawCount = *(int*)world.PreparedDrawCountPtr;
            if (drawCount > 0)
            {
                Material.Shader.Use();
                Material.Shader.SetInt("useDrawData", 1);
                Material.Shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, world.PreparedDrawDataSSBO);
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, world.CompactedAtlasSSBO);
                GL.BindVertexArray(Vao!);
                GL.BindBuffer(BufferTarget.DrawIndirectBuffer, world.PreparedIndirectCmdBuffer);
                GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, IntPtr.Zero, drawCount, 0);
                Log.CheckGlError();
                return;
            }
        }
        // Defer swapping pending compaction until after issuing draws to avoid mid-pass buffer mismatches
        // If GPU compaction is available, consume it directly; otherwise use fixed atlas path
        var hasCompaction = world.CompactedChunkIndices != null && world.CompactedBases != null && world.CompactedCounts != null && world.CompactedAtlasSSBO != 0;
        if (hasCompaction)
        {
            var version = world.CompactionVersion;
            var indices = world.CompactedChunkIndices!;
            var bases = world.CompactedBases!;
            var counts = world.CompactedCounts!;
            // Filter draws to current surrounding set to avoid rendering evicted chunks
            var desired = new HashSet<int>(world.SurroundingChunkIndices);
            var filtered = new List<int>(indices.Length);
            for (int i = 0; i < indices.Length; i++) if (desired.Contains(indices[i])) filtered.Add(i);
            var drawCount = filtered.Count;
            if (drawCount == 0)
            {
                RenderedBlocks = 0;
                return;
            }

            if (UseGpuFrustumCulling)
            {
                // Ensure/resize frustum compute buffers and upload filtered indices in draw order
                EnsureOrResizeBuffer(ref compactedIndicesSSBO, "compactedIndicesSSBO", drawCount * sizeof(int));
                var frustumIndices = new int[drawCount];
                for (int i = 0; i < drawCount; i++) frustumIndices[i] = indices[filtered[i]];
                // Upload via unsynchronized mapped range to avoid pixel-path sync
                var bytes = drawCount * sizeof(int);
                var mapped = GL.MapNamedBufferRange(compactedIndicesSSBO, IntPtr.Zero, bytes,
                    BufferAccessMask.MapWriteBit | BufferAccessMask.MapInvalidateBufferBit | BufferAccessMask.MapUnsynchronizedBit);
                if (mapped != IntPtr.Zero && drawCount > 0)
                {
                    unsafe { fixed (int* src = &frustumIndices[0]) { System.Buffer.MemoryCopy(src, (void*)mapped, (long)bytes, (long)bytes); } }
                    GL.UnmapNamedBuffer(compactedIndicesSSBO);
                }
                else
                {
                    // Recreate buffer with persistent mapping and retry once to avoid pixel-path sync
                    GL.DeleteBuffer(compactedIndicesSSBO);
                    CreateOrResizeMappedBuffer(ref compactedIndicesSSBO, "compactedIndicesSSBO", bytes, out var map2, out var _);
                    if (map2 != IntPtr.Zero && drawCount > 0)
                    {
                        unsafe { fixed (int* src = &frustumIndices[0]) { System.Buffer.MemoryCopy(src, (void*)map2, (long)bytes, (long)bytes); } }
                    }
                    else
                    {
                        // Disable frustum culling for this frame to avoid sync and invalid state
                        GL.DeleteBuffer(compactedIndicesSSBO);
                        compactedIndicesSSBO = 0;
                        UseGpuFrustumCulling = false;
                    }
                }
                EnsureOrResizeBuffer(ref drawVisibleSSBO, "drawVisibleSSBO", drawCount * sizeof(int));
            }

            // Ensure draw buffers capacity (prefer prepared handles)
            var ddTarget = world.PreparedDrawDataSSBO != 0 ? world.PreparedDrawDataSSBO : drawDataSSBO;
            var icTarget = world.PreparedIndirectCmdBuffer != 0 ? world.PreparedIndirectCmdBuffer : indirectCmdBuffer;
            var ddSize = drawCount * Unsafe.SizeOf<DrawDataGpu>();
            GL.GetNamedBufferParameter(ddTarget, BufferParameterName.BufferSize, out int currentDdSize);
            if (currentDdSize < ddSize && world.PreparedDrawDataSSBO == 0)
            {
                CreateOrResizeMappedBuffer(ref drawDataSSBO, "voxelDrawData_SSBO", ddSize, out drawDataMap, out drawDataMapSizeBytes);
                ddTarget = drawDataSSBO;
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, ddTarget);
            }
            var icSize = drawCount * GlIndirectCmdSize;
            GL.GetNamedBufferParameter(icTarget, BufferParameterName.BufferSize, out int currentIcSize);
            if (currentIcSize < icSize && world.PreparedIndirectCmdBuffer == 0)
            {
                CreateOrResizeMappedBuffer(ref indirectCmdBuffer, "voxelIndirectCmdBuffer", icSize, out indirectMap, out indirectMapSizeBytes);
                icTarget = indirectCmdBuffer;
            }

            var ddArray = new DrawDataGpu[drawCount];
            var cmdArray = new World.DrawElementsIndirectCommand[drawCount];
            RenderedBlocks = 0;
            // Compute player's current chunk index to correlate draw mapping
            var camPos = world.Camera.Position;
            var pChunkX = (int)((camPos.X + 0.5f) / VoxelHelper.ChunkSideSize);
            var pChunkZ = (int)((camPos.Z + 0.5f) / VoxelHelper.ChunkSideSize);
            var playerChunkIdx = pChunkX + pChunkZ * VoxelHelper.WorldChunksXZ;
            // Compute atlas capacity in elements to validate base/count ranges
            GL.GetNamedBufferParameter(world.CompactedAtlasSSBO, BufferParameterName.BufferSize, out int atlasBytes);
            var atlasElemCapacity = atlasBytes / Unsafe.SizeOf<GpuBlockState>();
            for (int i = 0; i < drawCount; i++)
            {
                var src = filtered[i];
                var idx = indices[src];
                // Derive world position directly from chunk index to avoid dependency on CPU upload
                var cx = idx % VoxelHelper.WorldChunksXZ;
                var cz = idx / VoxelHelper.WorldChunksXZ;
                var pos = new Vector3(cx * VoxelHelper.ChunkSideSize, 0, cz * VoxelHelper.ChunkSideSize);
                var isVisible = true; // visibility is further culled by compute frustum
                var model = Matrix4.Identity;
                model.Row3.Xyz = pos;
                int outlinedLocal = -1;
                if (PickedBlock != null && VoxelHelper.GetChunkIndexFromPositionGlobal(PickedBlock.Value.GlobalPosition) == idx)
                {
                    // Compute local 3D voxel index (vi) for the picked block within this chunk
                    var gp = PickedBlock.Value.GlobalPosition;
                    var lxPick = gp.X - (int)pos.X;
                    var lyPick = gp.Y - 0; // chunk Y origin is 0
                    var lzPick = gp.Z - (int)pos.Z;
                    if ((uint)lxPick < (uint)VoxelHelper.ChunkSideSize && (uint)lzPick < (uint)VoxelHelper.ChunkSideSize && (uint)lyPick < (uint)VoxelHelper.ChunkYSize)
                    {
                        int area = VoxelHelper.ChunkSideSizeSquare;
                        outlinedLocal = lxPick + lzPick * VoxelHelper.ChunkSideSize + lyPick * area;
                    }
                }

                ddArray[i] = new DrawDataGpu
                {
                    Model = model,
                    ChunkPos = new Vector4(pos.X, pos.Y, pos.Z, 1f),
                    BlocksBase = bases[src],
                    ChunkSize = VoxelHelper.ChunkSideSize,
                    OutlinedLocalIndex = outlinedLocal,
                    Enabled = isVisible ? 1 : 0
                };
                // Clamp instance count to valid atlas range for this draw to avoid out-of-bounds
                var baseElem = bases[src];
                if (baseElem < 0) baseElem = 0;
                var inst = counts[src];
                if (inst < 0) inst = 0;
                if (inst > MaxBlocksPerChunk) inst = MaxBlocksPerChunk;
                var maxAvail = atlasElemCapacity - baseElem;
                if (maxAvail < 0) maxAvail = 0;
                if (inst > maxAvail) inst = maxAvail;
                cmdArray[i] = new World.DrawElementsIndirectCommand
                {
                    Count = (uint)Vao!.DataLength,
                    InstanceCount = (uint)(isVisible ? inst : 0),
                    FirstIndex = 0,
                    BaseVertex = 0,
                    BaseInstance = (uint)i
                };
                if (isVisible) RenderedBlocks += inst;
            }

            // sampling logs removed

            if (world.PreparedDrawDataSSBO == 0)
            {
                if (drawDataMap != IntPtr.Zero)
                    CopyArrayToMapped(drawDataMap, 0, ddArray, drawCount);
                else
                    GL.NamedBufferSubData(ddTarget, IntPtr.Zero, drawCount * Unsafe.SizeOf<DrawDataGpu>(), ddArray);
            }

            // Copy commands; struct is tightly packed (20 bytes) so array layout matches GL
            if (world.PreparedIndirectCmdBuffer == 0)
            {
                if (indirectMap != IntPtr.Zero)
                    CopyArrayToMapped(indirectMap, 0, cmdArray, drawCount);
                else
                    GL.NamedBufferSubData(icTarget, IntPtr.Zero, drawCount * GlIndirectCmdSize, cmdArray);
            }
            GL.MemoryBarrier(MemoryBarrierFlags.ClientMappedBufferBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.CommandBarrierBit);

            var preCullRendered = RenderedBlocks;
            // Dispatch frustum culling compute to set per-draw visibility (optional)
            if (UseGpuFrustumCulling)
            {
                RunFrustumCullingCompute(drawCount);
            }
            else
            {
                // No GPU frustum: VS ignores drawVisible; do not bind/clear it
            }

            // If compaction changed mid-pass, skip drawing this frame to avoid mismatched buffers
            if (version != world.CompactionVersion)
            {
                RenderedBlocks = 0;
                return;
            }

            // Bind compacted atlas
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, world.CompactedAtlasSSBO);
            if (UseGpuFrustumCulling && drawVisibleSSBO != 0)
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, drawVisibleSSBO);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, drawDataSSBO);
            Material.Shader.SetInt("useDrawData", 1);
            // Force legacy selection uniform to disabled to avoid leaking outline to other draws
            if (Material.Shader.UniformExists("outlinedBlockId")) Material.Shader.SetInt("outlinedBlockId", -1);

            // Ensure shader knows chunk grid size for outline mapping in frag
            Material.Shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
            // Safety: ensure indirect buffer is large enough for drawCount commands (5 uints each = 20 bytes)
            GL.GetNamedBufferParameter(indirectCmdBuffer, BufferParameterName.BufferSize, out int icBufSize);
            var requiredIcBytes = drawCount * GlIndirectCmdSize;
            var requiredIcBytesMin = drawCount * GlIndirectCmdSize; // GL spec minimum layout
            if (icBufSize < requiredIcBytesMin)
            {
                // Skip this frame to avoid GL_INVALID_VALUE on MultiDraw
                RenderedBlocks = 0;
                Log.Warn($"DrawMdi: indirect buffer too small. have={icBufSize}, need>={requiredIcBytesMin}, drawCount={drawCount}");
                return;
            }

            GL.BindVertexArray(Vao!);
            var mdiHandle = world.PreparedIndirectCmdBuffer != 0 ? world.PreparedIndirectCmdBuffer : icTarget;
            GL.BindBuffer(BufferTarget.DrawIndirectBuffer, mdiHandle);
            // Tightly packed commands; use stride=0 to match GL convention universally
            GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, IntPtr.Zero, drawCount, 0);
            Log.CheckGlError();
            lastCompactionVersion = world.CompactionVersion;
            // Now safe to swap pending compaction for the next frame
            try { world.TrySwapPendingCompaction(); } catch { }
            return;
        }

        // No legacy fixed-atlas fallback; rely solely on GPU compaction.
        RenderedBlocks = 0;
        try { world.TrySwapPendingCompaction(); } catch { }
        return;
    }

    private void EnsureOrResizeBuffer(ref uint handle, string label, int sizeBytes)
    {
        if (handle == 0)
        {
            GL.CreateBuffers(1, out handle);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, handle, -1, label);
            // Allow mapping for write uploads when needed
            var flags = BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapWriteBit;
            GL.NamedBufferStorage(handle, sizeBytes, IntPtr.Zero, flags);
            return;
        }
        GL.GetNamedBufferParameter(handle, BufferParameterName.BufferSize, out int current);
        if (current < sizeBytes)
        {
            GL.DeleteBuffer(handle);
            GL.CreateBuffers(1, out handle);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, handle, -1, label);
            var flags = BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapWriteBit;
            GL.NamedBufferStorage(handle, sizeBytes, IntPtr.Zero, flags);
        }
    }

    private Shader? frustumShader;
    private Shader? frustumApplyShader;
    private Shader? frustumCountVisibleShader;
    private long lastFrustumLogMs;
    private void RunFrustumCullingCompute(int drawCount)
    {
        frustumShader ??= new Shader("Shaders/compute-frustum-chunks.comp", ShaderType.ComputeShader);
        frustumShader.Use();

        // Bind SSBOs
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, drawVisibleSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 11, compactedIndicesSSBO);

        // Upload uniforms
        frustumShader.SetInt("drawCount", drawCount);
        frustumShader.SetInt("worldChunksXZ", VoxelHelper.WorldChunksXZ);
        frustumShader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        frustumShader.SetInt("chunkYSize", VoxelHelper.ChunkYSize);

        // Compute frustum planes from camera
        var planes = ComputeFrustumPlanes(world.Camera);
        frustumShader.SetVector4("fp0", ref planes[0]);
        frustumShader.SetVector4("fp1", ref planes[1]);
        frustumShader.SetVector4("fp2", ref planes[2]);
        frustumShader.SetVector4("fp3", ref planes[3]);
        frustumShader.SetVector4("fp4", ref planes[4]);
        frustumShader.SetVector4("fp5", ref planes[5]);

        GL.DispatchCompute((drawCount + 63) / 64, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);

        // Apply mask to indirect commands: zero out InstanceCount for culled draws
        if (ApplyIndirectCulling)
        {
            frustumApplyShader ??= new Shader("Shaders/compute-frustum-apply.comp", ShaderType.ComputeShader);
            frustumApplyShader.Use();
            frustumApplyShader.SetInt("drawCount", drawCount);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, drawVisibleSSBO);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12, indirectCmdBuffer);
            GL.DispatchCompute((drawCount + 63) / 64, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);
        }
        else
        {
            // No-op: when not applying indirect culling, the VS ignores drawVisible mask
        }
        // Compute sum of InstanceCount into a persistently mapped 4-byte buffer and read without stalls
        if (drawnTotalSSBO == 0)
        {
            GL.CreateBuffers(1, out drawnTotalSSBO);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, drawnTotalSSBO, -1, "drawnTotalSSBO");
            var flags = BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit | BufferStorageFlags.DynamicStorageBit;
            GL.NamedBufferStorage(drawnTotalSSBO, sizeof(uint), IntPtr.Zero, flags);
            drawnTotalPtr = GL.MapNamedBufferRange(drawnTotalSSBO, IntPtr.Zero, sizeof(uint), BufferAccessMask.MapReadBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit);
        }
        // Reset via compute to avoid pixel-path sync
        var clearU32 = new Shader("Shaders/compute-clear-uint.comp", ShaderType.ComputeShader);
        clearU32.Use();
        clearU32.SetInt("valueIn", 0);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 14, drawnTotalSSBO);
        GL.DispatchCompute(1, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        var sumShader = new Shader("Shaders/compute-sum-indirect.comp", ShaderType.ComputeShader);
        sumShader.Use();
        sumShader.SetInt("drawCount", drawCount);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12, indirectCmdBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 14, drawnTotalSSBO);
        GL.DispatchCompute((drawCount + 63) / 64, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
        unsafe { RenderedBlocks = *(int*)drawnTotalPtr; }
        // Also count visible draws for diagnostics (chunks in frustum)
        if (visibleDrawsSSBO == 0)
        {
            GL.CreateBuffers(1, out visibleDrawsSSBO);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, visibleDrawsSSBO, -1, "visibleDrawsSSBO");
            var flags2 = BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit | BufferStorageFlags.DynamicStorageBit;
            GL.NamedBufferStorage(visibleDrawsSSBO, sizeof(uint), IntPtr.Zero, flags2);
            visibleDrawsPtr = GL.MapNamedBufferRange(visibleDrawsSSBO, IntPtr.Zero, sizeof(uint), BufferAccessMask.MapReadBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit);
        }
        // Reset via compute
        clearU32.Use();
        clearU32.SetInt("valueIn", 0);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 14, visibleDrawsSSBO);
        GL.DispatchCompute(1, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        frustumCountVisibleShader ??= new Shader("Shaders/compute-count-visible.comp", ShaderType.ComputeShader);
        frustumCountVisibleShader.Use();
        frustumCountVisibleShader.SetInt("drawCount", drawCount);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 12, indirectCmdBuffer);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 15, visibleDrawsSSBO);
        GL.DispatchCompute((drawCount + 63) / 64, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.AllBarrierBits);
        unsafe { VisibleDraws = *(int*)visibleDrawsPtr; }
        // Frustum metrics are available via VisibleDraws/RenderedBlocks; suppress periodic logs to reduce noise
        lastFrustumLogMs = System.Environment.TickCount64;
    }

    private static Vector4[] ComputeFrustumPlanes(ICamera camera)
    {
        // Use the existing Frustum extraction for consistency
        var fr = new OpenRender.Core.Culling.Frustum();
        fr.Update(camera);
        return fr.Planes;
    }

    private unsafe void CopyArrayToMapped<T>(IntPtr basePtr, int elementBaseOffset, T[] data, int count) where T : unmanaged
    {
        if (count <= 0 || data.Length == 0) return;
        var elemSize = Unsafe.SizeOf<T>();
        var dst = (byte*)basePtr + (elementBaseOffset * elemSize);
        fixed (T* src = &data[0])
        {
            System.Buffer.MemoryCopy(src, dst, long.MaxValue, (long)count * elemSize);
        }
    }

    // Diagnostics for GPU frustum
    private uint visibleDrawsSSBO;
    private IntPtr visibleDrawsPtr;
    public int VisibleDraws { get; private set; }
}
