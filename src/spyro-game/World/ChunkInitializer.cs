using OpenRender;
using OpenRender.Core.Rendering;
using OpenTK.Graphics.OpenGL4;
using SpyroGame.World;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SpyroGame;

// Requires: <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
internal sealed class ChunkInitializer : IDisposable
{
    // --- constants ---
    private static readonly int VoxelsCount =
        VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize;

    // --- deps/state ---
    private readonly VoxelWorld world;
    private readonly TerrainBuilder terrainBuilder;
    private readonly Shader shader;

    // SSBOs
    private uint heightSSBO;           // binding=0 (per-dispatch chunk heightmaps)
    private uint blockTypeSSBO;        // binding=1 (output; persistently mapped for readback)
    private uint chunkIndicesSSBO;     // binding=2 (input; subset of world chunk indices)
    private uint columnHeightsSSBO;    // binding=3 (output normalized heights per column)
    private uint blockAttribSSBO;      // binding=4 (output block visibility/AO)

    // persistent map for blockTypeSSBO
    private IntPtr blockTypePtr = IntPtr.Zero;
    private IntPtr columnHeightsPtr = IntPtr.Zero;
    private IntPtr blockAttribPtr = IntPtr.Zero;
    private int blockTypeCapacityBytes;
    private int chunkIndicesCapacity;  // ints allocated in chunkIndicesSSBO
    private int heightCapacityFloats;
    private int columnHeightsCapacity;
    private int blockAttribCapacityBytes;

    public ChunkInitializer(VoxelWorld world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        terrainBuilder = world.terrainBuilder;
        shader = new Shader("Shaders/compute-chunk.comp", ShaderType.ComputeShader);
    }

    // ---- public APIs ----

    // One-shot (all indices at once). Runs on GL thread.
    public void ProcessChunkData(int[] chunkIndices)
    {
        if (chunkIndices == null || chunkIndices.Length == 0) return;

        var pending = new List<int>(chunkIndices.Length);
        foreach (var idx in chunkIndices)
        {
            var chunk = world.GetOrCreateChunkContainer(idx);
            if (chunk.IsInitialized && chunk.IsProcessed)
                continue;
            pending.Add(idx);
        }
        if (pending.Count == 0) return;
        chunkIndices = pending.ToArray();

        var chunkCount = chunkIndices.Length;

        EnsureChunkIndicesCapacity(chunkCount);
        GL.NamedBufferSubData(chunkIndicesSSBO, 0, chunkCount * sizeof(int), chunkIndices);

        EnsureHeightCapacity(chunkCount);
        UploadHeights(chunkIndices);

        var bytesPerChunk = VoxelsCount * Unsafe.SizeOf<uint>();
        EnsureBlockTypeCapacity(bytesPerChunk * chunkCount);
        EnsureBlockAttribCapacity(bytesPerChunk * chunkCount);
        EnsureColumnHeightsOutputCapacity(chunkCount);

        DispatchAndReadback(chunkIndices, 0, chunkCount);
    }

    // Asynchronous pipeline: submit a batch without blocking; results applied later.
    private bool batchInFlight;
    private int[]? batchIndices;
    private int batchCount;
    private IntPtr batchFence = IntPtr.Zero;

    public bool HasInFlightBatch => batchInFlight;

    public bool SubmitBatch(int[] chunkIndices)
    {
        if (chunkIndices == null || chunkIndices.Length == 0) return false;
        if (batchInFlight) return false;

        var chunkCount = chunkIndices.Length;
        EnsureChunkIndicesCapacity(chunkCount);
        GL.NamedBufferSubData(chunkIndicesSSBO, 0, chunkCount * sizeof(int), chunkIndices);

        EnsureHeightCapacity(chunkCount);
        UploadHeights(chunkIndices);

        var bytesPerChunk = VoxelsCount * Unsafe.SizeOf<uint>();
        EnsureBlockTypeCapacity(bytesPerChunk * chunkCount);
        EnsureBlockAttribCapacity(bytesPerChunk * chunkCount);
        EnsureColumnHeightsOutputCapacity(chunkCount);

        // Bind and dispatch
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, heightSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkIndicesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, blockAttribSSBO);
        shader.Use();
        shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shader.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        shader.SetInt("waterLevel", VoxelHelper.WaterLevel);
        Log.CheckGlError(nameof(SubmitBatch) + " before DispatchCompute");

        GL.DispatchCompute(chunkCount, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        Log.CheckGlError(nameof(SubmitBatch) + " after DispatchCompute");

        batchFence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        batchIndices = chunkIndices;
        batchCount = chunkCount;
        batchInFlight = true;
        return true;
    }

    public bool TryCompleteBatch(out int[] completed)
    {
        completed = Array.Empty<int>();
        if (!batchInFlight || batchFence == IntPtr.Zero || batchIndices is null || batchCount == 0)
            return false;

        var status = GL.ClientWaitSync(batchFence, 0, 0);
        if (status == WaitSyncStatus.TimeoutExpired)
            return false; // not ready yet

        GL.DeleteSync(batchFence);
        batchFence = IntPtr.Zero;

        // Apply results
        var indices = batchIndices;
        var count = batchCount;
        batchIndices = null;
        batchCount = 0;
        batchInFlight = false;

        ApplyBatch(indices, 0, count);
        completed = indices;
        return true;
    }

    private unsafe void ApplyBatch(int[] chunkIndices, int start, int count)
    {
        var bytesPerChunk = VoxelsCount * Unsafe.SizeOf<uint>();
        var basePtr = (byte*)blockTypePtr;
        var columnArea = VoxelHelper.ChunkSideSizeSquare;
        var columnPtr = (byte*)columnHeightsPtr;
        var attribPtr = (byte*)blockAttribPtr;

        // Keep CPU work small; reuse arrays where possible would be ideal, but keep simple for now.
        for (var i = 0; i < count; i++)
        {
            var srcU32 = (uint*)(basePtr + i * bytesPerChunk);
            var srcHeights = (uint*)(columnPtr + i * columnArea * sizeof(uint));
            var srcAttribs = (uint*)(attribPtr + i * bytesPerChunk);
            var worldChunkIndex = chunkIndices[start + i];
            var chunk = world.GetOrCreateChunkContainer(worldChunkIndex);

            var blockTypes = new BlockType[VoxelsCount];
            for (var v = 0; v < VoxelsCount; v++)
            {
                blockTypes[v] = (BlockType)srcU32[v];
            }

            var columnHeights = new int[columnArea];
            for (var c = 0; c < columnArea; c++)
            {
                columnHeights[c] = (int)srcHeights[c];
            }

            var blockAttributes = new uint[VoxelsCount];
            for (var v = 0; v < VoxelsCount; v++)
            {
                blockAttributes[v] = srcAttribs[v];
            }

            var columnInfos = new ColumnInfo[VoxelHelper.ChunkSideSizeSquare];
            world.terrainBuilder.FillChunkColumnInfo(worldChunkIndex, columnHeights, columnInfos);

            var data = new TerrainBuilder.ChunkGenerationData(blockTypes, columnHeights, columnInfos, blockAttributes);
            chunk.ApplyGenerationData(world.terrainBuilder, data);
            chunk.ApplyBlockAttributes(blockAttributes);

            // Fix seams by refreshing border lighting for borders only (cheap)
            chunk.RecomputeLighting(force: true, includeNeighborData: true, bordersOnly: true);

            var hasChanges = world.LoadChangedChunkBlocks(chunk);
            if (hasChanges)
            {
                chunk.RecomputeLighting(force: true, includeNeighborData: true);
            }
        }
    }

    // ---- internals ----

    private void EnsureChunkIndicesCapacity(int count)
    {
        if (chunkIndicesSSBO == 0)
        {
            GL.CreateBuffers(1, out chunkIndicesSSBO);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, chunkIndicesSSBO, -1, "chunkIndicesSSBO");
        }
        if (count > chunkIndicesCapacity)
        {
            GL.NamedBufferStorage(chunkIndicesSSBO, count * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            chunkIndicesCapacity = count;
        }
    }

    private void EnsureHeightCapacity(int chunkCount)
    {
        if (chunkCount == 0) return;
        var requiredFloats = chunkCount * VoxelHelper.ChunkSideSizeSquare;

        if (heightSSBO == 0)
        {
            GL.CreateBuffers(1, out heightSSBO);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, heightSSBO, -1, "chunkHeightSSBO");
        }

        if (requiredFloats > heightCapacityFloats)
        {
            GL.NamedBufferStorage(heightSSBO, requiredFloats * sizeof(float), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            heightCapacityFloats = requiredFloats;
        }
    }

    private void UploadHeights(int[] chunkIndices)
    {
        var chunkArea = VoxelHelper.ChunkSideSizeSquare;
        var totalFloats = chunkIndices.Length * chunkArea;
        if (totalFloats == 0) return;

        var buffer = ArrayPool<float>.Shared.Rent(totalFloats);
        try
        {
            var span = buffer.AsSpan(0, totalFloats);
            for (var i = 0; i < chunkIndices.Length; i++)
            {
                terrainBuilder.FillChunkHeight01(chunkIndices[i], span.Slice(i * chunkArea, chunkArea));
            }

            GL.NamedBufferSubData(heightSSBO, 0, totalFloats * sizeof(float), buffer);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private unsafe void EnsureBlockTypeCapacity(int requiredBytes)
    {
        if (blockTypeSSBO != 0)
        {
            GL.GetNamedBufferParameter(blockTypeSSBO, BufferParameterName.BufferSize, out int current);
            if (current >= requiredBytes && blockTypePtr != IntPtr.Zero) return;

            // drop old buffer if size/mapping mismatches
            GL.DeleteBuffer(blockTypeSSBO);
            blockTypePtr = IntPtr.Zero;
            blockTypeCapacityBytes = 0;
        }

        GL.CreateBuffers(1, out blockTypeSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, blockTypeSSBO, -1, "blockTypeSSBO");

        var storageFlags =
            BufferStorageFlags.MapReadBit |
            BufferStorageFlags.MapPersistentBit |
            BufferStorageFlags.MapCoherentBit;

        GL.NamedBufferStorage(blockTypeSSBO, requiredBytes, IntPtr.Zero, storageFlags);

        blockTypePtr = GL.MapNamedBufferRange(
            blockTypeSSBO,
            IntPtr.Zero,
            requiredBytes,
            BufferAccessMask.MapReadBit |
            BufferAccessMask.MapPersistentBit |
            BufferAccessMask.MapCoherentBit
        );
        blockTypeCapacityBytes = requiredBytes;
    }

    private unsafe void EnsureBlockAttribCapacity(int requiredBytes)
    {
        if (blockAttribSSBO != 0)
        {
            GL.GetNamedBufferParameter(blockAttribSSBO, BufferParameterName.BufferSize, out int current);
            if (current >= requiredBytes && blockAttribPtr != IntPtr.Zero) return;

            GL.DeleteBuffer(blockAttribSSBO);
            blockAttribPtr = IntPtr.Zero;
            blockAttribCapacityBytes = 0;
        }

        GL.CreateBuffers(1, out blockAttribSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, blockAttribSSBO, -1, "blockAttribSSBO");
        GL.NamedBufferStorage(blockAttribSSBO, requiredBytes, IntPtr.Zero,
            BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit);
        blockAttribPtr = GL.MapNamedBufferRange(blockAttribSSBO, IntPtr.Zero, requiredBytes,
            BufferAccessMask.MapReadBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit);
        blockAttribCapacityBytes = requiredBytes;
    }


    private unsafe void EnsureColumnHeightsOutputCapacity(int chunkCount)
    {
        if (chunkCount == 0) return;
        var requiredBytes = chunkCount * VoxelHelper.ChunkSideSizeSquare * sizeof(uint);

        if (columnHeightsSSBO != 0)
        {
            GL.GetNamedBufferParameter(columnHeightsSSBO, BufferParameterName.BufferSize, out int current);
            if (current >= requiredBytes && columnHeightsPtr != IntPtr.Zero) return;

            GL.DeleteBuffer(columnHeightsSSBO);
            columnHeightsPtr = IntPtr.Zero;
            columnHeightsCapacity = 0;
        }

        GL.CreateBuffers(1, out columnHeightsSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, columnHeightsSSBO, -1, "columnHeightsSSBO");
        GL.NamedBufferStorage(columnHeightsSSBO, requiredBytes, IntPtr.Zero,
            BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit);
        columnHeightsPtr = GL.MapNamedBufferRange(columnHeightsSSBO, IntPtr.Zero, requiredBytes,
            BufferAccessMask.MapReadBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit);
        columnHeightsCapacity = requiredBytes;
    }

    private void DispatchAndReadback(int[] chunkIndices, int start, int count)
    {
        var sw = Stopwatch.StartNew();

        // bind common SSBOs
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, heightSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkIndicesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, blockAttribSSBO);

        // uniforms
        shader.Use();
        shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shader.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        shader.SetInt("waterLevel", VoxelHelper.WaterLevel);
        Log.CheckGlError(nameof(DispatchAndReadback) + " before DispatchCompute");

        // dispatch this batch
        GL.DispatchCompute(count, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        Log.CheckGlError(nameof(DispatchAndReadback) + " after DispatchCompute");

        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, ulong.MaxValue);
        GL.DeleteSync(fence);
        Log.Info($"DispatchAndReadback compute time: {sw.ElapsedMilliseconds:N2} ms");
        sw.Restart();

        unsafe
        {
            var bytesPerChunk = VoxelsCount * Unsafe.SizeOf<uint>();
            var basePtr = (byte*)blockTypePtr;
            var columnArea = VoxelHelper.ChunkSideSizeSquare;
            var columnPtr = (byte*)columnHeightsPtr;
            var attribPtr = (byte*)blockAttribPtr;

            Parallel.For(0, count, i =>
            {
                var srcU32 = (uint*)(basePtr + i * bytesPerChunk);
                var srcHeights = (uint*)(columnPtr + i * columnArea * sizeof(uint));
                var srcAttribs = (uint*)(attribPtr + i * bytesPerChunk);
                var worldChunkIndex = chunkIndices[start + i];
                var chunk = world.GetOrCreateChunkContainer(worldChunkIndex);

                var blockTypes = new BlockType[VoxelsCount];
                for (var v = 0; v < VoxelsCount; v++)
                {
                    blockTypes[v] = (BlockType)srcU32[v];
                }

                var columnHeights = new int[columnArea];
                for (var c = 0; c < columnArea; c++)
                {
                    columnHeights[c] = (int)srcHeights[c];
                }

                var blockAttributes = new uint[VoxelsCount];
                for (var v = 0; v < VoxelsCount; v++)
                {
                    blockAttributes[v] = srcAttribs[v];
                }

                var columnInfos = new ColumnInfo[VoxelHelper.ChunkSideSizeSquare];
                world.terrainBuilder.FillChunkColumnInfo(worldChunkIndex, columnHeights, columnInfos);

                var data = new TerrainBuilder.ChunkGenerationData(blockTypes, columnHeights, columnInfos, blockAttributes);
                chunk.ApplyGenerationData(world.terrainBuilder, data);
                chunk.ApplyBlockAttributes(blockAttributes);

                // Re-run lighting for borders with neighbor data so GPU seams match CPU results.
                chunk.RecomputeLighting(force: true, includeNeighborData: true, bordersOnly: true);

                var hasChanges = world.LoadChangedChunkBlocks(chunk);
                if (hasChanges)
                {
                    chunk.RecomputeLighting(force: true, includeNeighborData: true);
                }
            });
        }
        Log.Info($"DispatchAndReadback readback/apply time: {sw.ElapsedMilliseconds:N2} ms");
    }

    public void Dispose()
    {
        if (blockTypeSSBO != 0)
        {
            GL.DeleteBuffer(blockTypeSSBO);
            blockTypeSSBO = 0;
            blockTypePtr = IntPtr.Zero;
            blockTypeCapacityBytes = 0;
        }
        if (chunkIndicesSSBO != 0)
        {
            GL.DeleteBuffer(chunkIndicesSSBO);
            chunkIndicesSSBO = 0;
            chunkIndicesCapacity = 0;
        }
        if (heightSSBO != 0)
        {
            GL.DeleteBuffer(heightSSBO);
            heightSSBO = 0;
            heightCapacityFloats = 0;
        }
        if (columnHeightsSSBO != 0)
        {
            GL.DeleteBuffer(columnHeightsSSBO);
            columnHeightsSSBO = 0;
            columnHeightsPtr = IntPtr.Zero;
            columnHeightsCapacity = 0;
        }
        if (blockAttribSSBO != 0)
        {
            GL.DeleteBuffer(blockAttribSSBO);
            blockAttribSSBO = 0;
            blockAttribPtr = IntPtr.Zero;
            blockAttribCapacityBytes = 0;
        }
        world.OnChunkInitializerDisposed(this);
        //  TODO: implement shader.Dispose();
    }
}
