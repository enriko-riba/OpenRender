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
    private readonly Shader shaderBorders;
    private readonly Shader shaderCompactCount;
    private readonly Shader shaderCompactWrite;
    // removed unused edit passes (breaks/vis local) after integrating into Pass1

    // SSBOs
    private uint heightSSBO;           // binding=0 (per-dispatch chunk heightmaps)
    private uint blockTypeSSBO;        // binding=1 (output; persistently mapped for readback)
    private uint chunkIndicesSSBO;     // binding=2 (input; subset of world chunk indices)
    private uint columnHeightsSSBO;    // binding=3 (output normalized heights per column)
    private uint blockAttribSSBO;      // binding=4 (output block visibility/AO)
    private uint neighborGroupsSSBO;   // binding=5 (neighbor group ids per chunk)
    private uint remapSSBO;            // binding=13 (primary index -> source group index)

    // persistent map for blockTypeSSBO
    private IntPtr blockTypePtr = IntPtr.Zero;
    private IntPtr columnHeightsPtr = IntPtr.Zero;
    private IntPtr blockAttribPtr = IntPtr.Zero;
    private int blockTypeCapacityBytes;
    private int chunkIndicesCapacity;  // ints allocated in chunkIndicesSSBO
    private int heightCapacityFloats;
    private int columnHeightsCapacity;
    private int blockAttribCapacityBytes;
    private int neighborGroupsCapacity;
    private int remapCapacityBytes;
    private uint countsBuffer;
    private IntPtr countsPtr = IntPtr.Zero;
    private int countsCapacityBytes;
    private static long lastDiagnosticsMs;
    private long lastPublishTick;

    // Fence to serialize GPU passes that reuse SSBOs (indices/heights/attribs)
    private IntPtr inFlightFence = IntPtr.Zero;

    // Prevent overlapping ProcessChunkData() calls from different callers
    private int syncInProgress; // 0 = idle, 1 = running

    private void WaitForPreviousGpu()
    {
        try
        {
            var fence = inFlightFence;
            if (fence == IntPtr.Zero) return;
            // Block until the previous compute work that uses shared SSBOs completes
            var status = GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, 50_000_000);
            // Regardless of status, delete fence to avoid leaks; if timeout, GPU will catch up before next pass due to implicit flush
            try { GL.DeleteSync(fence); } catch { }
            inFlightFence = IntPtr.Zero;
        }
        catch { }
    }

    // Reusable break mask (3D bitset) buffer to avoid per-publish allocation
    private int breakMask3DSSBO;
    private int breakMask3DCapacityBytes;
    private IntPtr breakMask3DPtr = IntPtr.Zero;
    private int breakMask3DWordsPerChunk;

    private void EnsureBreakMask3DCapacity(int count)
    {
        int area = VoxelHelper.ChunkSideSizeSquare;
        int wordsPerChunk = ((area * VoxelHelper.ChunkYSize) + 31) / 32;
        int required = Math.Max(1, count) * wordsPerChunk * sizeof(uint);
        if (breakMask3DSSBO != 0)
        {
            GL.GetNamedBufferParameter(breakMask3DSSBO, BufferParameterName.BufferSize, out int current);
            if (current >= required && breakMask3DPtr != IntPtr.Zero)
            {
                breakMask3DWordsPerChunk = wordsPerChunk;
                return;
            }
            if (breakMask3DSSBO != 0) { GL.DeleteBuffer(breakMask3DSSBO); }
            breakMask3DSSBO = 0;
            breakMask3DCapacityBytes = 0;
            breakMask3DPtr = IntPtr.Zero;
        }
        GL.CreateBuffers(1, out breakMask3DSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, breakMask3DSSBO, -1, "breakMask3DSSBO");
        var flags = BufferStorageFlags.MapWriteBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit | BufferStorageFlags.DynamicStorageBit;
        GL.NamedBufferStorage(breakMask3DSSBO, required, IntPtr.Zero, flags);
        breakMask3DPtr = GL.MapNamedBufferRange(breakMask3DSSBO, IntPtr.Zero, required,
            BufferAccessMask.MapWriteBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit);
        breakMask3DCapacityBytes = required;
        breakMask3DWordsPerChunk = wordsPerChunk;
    }

    private unsafe void FillBreakMask3D(int[] chunkIndices, int start, int count, int[]? remapForMasks)
    {
        EnsureBreakMask3DCapacity(count);
        int wordsPerChunk = breakMask3DWordsPerChunk;
        int bytesPerChunk = wordsPerChunk * sizeof(uint);
        byte* basePtr = (byte*)breakMask3DPtr;
        // Zero full range for the publish to avoid stale bits
        System.Span<byte> span = new System.Span<byte>(basePtr, Math.Max(count,1) * bytesPerChunk);
        span.Clear();
        for (int i = 0; i < count; i++)
        {
            int idx = chunkIndices[start + i];
            var mask = world.GetBreakMask3D(idx);
            int mapped = (remapForMasks != null && remapForMasks.Length == count) ? remapForMasks[i] : i;
            byte* dst = basePtr + mapped * bytesPerChunk;
            if (mask is null) continue;
            fixed (byte* src = &mask[0])
            {
                System.Buffer.MemoryCopy(src, dst, bytesPerChunk, System.Math.Min(bytesPerChunk, mask.Length));
            }
        }
        GL.MemoryBarrier(MemoryBarrierFlags.ClientMappedBufferBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);
    }

    // Preallocate big enough buffers for the full surrounding set to avoid reallocations mid-flight
    public void PreallocateForMaxSurrounding()
    {
        var r = VoxelHelper.MaxDistanceInChunks;
        var maxChunks = (2 * r + 1) * (2 * r + 1);
        if (maxChunks <= 0) return;

        // chunk indices
        EnsureChunkIndicesCapacity(maxChunks);

        // heights
        EnsureHeightCapacity(maxChunks);

        // columns readback (for collision)
        EnsureColumnHeightsOutputCapacity(maxChunks);

        // counts buffer (int per chunk)
        EnsureCountsBuffer(maxChunks * sizeof(int));
    }

    public ChunkInitializer(VoxelWorld world)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        terrainBuilder = world.terrainBuilder;
        shader = new Shader("Shaders/compute-chunk.comp", ShaderType.ComputeShader);
        shaderBorders = new Shader("Shaders/compute-borders.comp", ShaderType.ComputeShader);
        shaderCompactCount = new Shader("Shaders/compute-compact-count.comp", ShaderType.ComputeShader);
        shaderCompactWrite = new Shader("Shaders/compute-compact-write.comp", ShaderType.ComputeShader);
        // integrated into compute-chunk; no separate edit passes
    }

    // ---- public APIs ----

    // One-shot (all indices at once). Runs on GL thread.
    public void ProcessChunkData(int[] chunkIndices)
    {
        if (System.Threading.Interlocked.Exchange(ref syncInProgress, 1) == 1)
        {
            // Already running; skip to avoid remap/SSBO races
            return;
        }
        try
        {
        if (chunkIndices == null || chunkIndices.Length == 0) return;

        // Deterministic order to avoid per-publish drawIndex/base mismatches
        var indicesSorted = (int[])chunkIndices.Clone();
        Array.Sort(indicesSorted);

        var chunkCount = indicesSorted.Length;
        try { Log.Info($"GPU Publish: submitting {chunkCount} chunks"); } catch { }

        // Ensure previous GPU work finished before reusing SSBOs
        WaitForPreviousGpu();
        EnsureChunkIndicesCapacity(chunkCount);
        // Upload indices via unsynchronized mapped range to avoid pixel-path sync
        {
            var bytes = chunkCount * sizeof(int);
            var mapped = GL.MapNamedBufferRange(chunkIndicesSSBO, IntPtr.Zero, bytes,
                BufferAccessMask.MapWriteBit | BufferAccessMask.MapInvalidateBufferBit | BufferAccessMask.MapUnsynchronizedBit);
            if (mapped != IntPtr.Zero && indicesSorted.Length > 0)
            {
                unsafe
                {
                    fixed (int* src = &indicesSorted[0])
                    {
                        System.Buffer.MemoryCopy(src, (void*)mapped, (long)bytes, (long)bytes);
                    }
                }
                GL.UnmapNamedBuffer(chunkIndicesSSBO);
            }
            else
            {
                GL.NamedBufferSubData(chunkIndicesSSBO, IntPtr.Zero, bytes, indicesSorted);
            }
        }

        // GPU computes heights; skip CPU height upload

        var bytesPerChunk_local = VoxelsCount * Unsafe.SizeOf<uint>();
        EnsureBlockTypeCapacity(bytesPerChunk_local * chunkCount);
        EnsureBlockAttribCapacity(bytesPerChunk_local * chunkCount);
        EnsureColumnHeightsOutputCapacity(chunkCount);

        DispatchAndReadback(indicesSorted, 0, chunkCount);
        // Serialize subsequent passes that will reuse SSBOs
        inFlightFence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref syncInProgress, 0);
        }
    }

    // Asynchronous pipeline: submit a batch without blocking; results applied later.
    private bool batchInFlight;
    private int[]? batchIndices;
    private int batchCount;
    private IntPtr batchFence = IntPtr.Zero;

    public bool HasInFlightBatch => batchInFlight;

    /// <summary>
    /// Append-only publish: generates and compacts only the provided primary chunk indices,
    /// repairs border AO/visibility via a padded dispatch (primaries + cardinals), and appends
    /// the new draws to the resident atlas and draw arrays. Existing draws remain intact.
    /// </summary>
    public bool AppendChunks(int[] primary)
    {
        if (primary == null || primary.Length == 0) return false;

        // Build padded batch: primary + unique 4-neighbors for border correctness
        var set = new HashSet<int>(primary);
        var paddedList = new List<int>(primary.Length * 2);
        paddedList.AddRange(primary);
        var side = VoxelHelper.WorldChunksXZ;
        foreach (var idx in primary)
        {
            var x = idx % side; var z = idx / side;
            int west = (x > 0) ? idx - 1 : -1;
            int east = (x + 1 < side) ? idx + 1 : -1;
            int north = (z > 0) ? idx - side : -1;
            int south = (z + 1 < side) ? idx + side : -1;
            if (west >= 0 && set.Add(west)) paddedList.Add(west);
            if (east >= 0 && set.Add(east)) paddedList.Add(east);
            if (north >= 0 && set.Add(north)) paddedList.Add(north);
            if (south >= 0 && set.Add(south)) paddedList.Add(south);
        }
        var padded = paddedList.ToArray();

        // Ensure previous GPU work finished before reusing SSBOs
        WaitForPreviousGpu();
        EnsureChunkIndicesCapacity(padded.Length);
        // Upload indices via unsynchronized mapped range to avoid pixel-path sync
        {
            var bytes = padded.Length * sizeof(int);
            var mapped = GL.MapNamedBufferRange(chunkIndicesSSBO, IntPtr.Zero, bytes,
                BufferAccessMask.MapWriteBit | BufferAccessMask.MapInvalidateBufferBit | BufferAccessMask.MapUnsynchronizedBit);
            if (mapped != IntPtr.Zero && padded.Length > 0)
            {
                unsafe
                {
                    fixed (int* src = &padded[0])
                    {
                        System.Buffer.MemoryCopy(src, (void*)mapped, (long)bytes, (long)bytes);
                    }
                }
                GL.UnmapNamedBuffer(chunkIndicesSSBO);
            }
            else
            {
                GL.NamedBufferSubData(chunkIndicesSSBO, IntPtr.Zero, bytes, padded);
            }
        }

        // Ensure storage
        var voxBytes = VoxelsCount * Unsafe.SizeOf<uint>();
        EnsureBlockTypeCapacity(voxBytes * padded.Length);
        EnsureBlockAttribCapacity(voxBytes * padded.Length);
        EnsureColumnHeightsOutputCapacity(padded.Length);

        // Pass G0: types + initial AO/visibility (+edits)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkIndicesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, blockAttribSSBO);
        FillBreakMask3D(padded, 0, padded.Length, null);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, breakMask3DSSBO);
        shader.Use();
        shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shader.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        shader.SetInt("waterLevel", VoxelHelper.WaterLevel);
        shader.SetInt("worldChunksXZ", VoxelHelper.WorldChunksXZ);
        shader.SetFloat("baseFreq", 1f / 180f);
        shader.SetFloat("warpFreq", 1f / 900f);
        shader.SetFloat("warpAmp", 8f);
        shader.SetInt("noiseSeed", world.Seed ^ 0x12345);
        GL.DispatchCompute(padded.Length, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);

        // Pass G1: border fix on padded set
        EnsureNeighborGroups(padded, 0, padded.Length);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, neighborGroupsSSBO);
        shaderBorders.Use();
        shaderBorders.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shaderBorders.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        GL.DispatchCompute(padded.Length, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);

        // Update CPU collision heights for primaries from columnHeights (persistently mapped)
        try
        {
            unsafe
            {
                int area = VoxelHelper.ChunkSideSizeSquare;
                if (columnHeightsPtr != IntPtr.Zero)
                {
                    var src = (uint*)columnHeightsPtr;
                    // By construction, padded starts with primaries in the same order
                    for (int i = 0; i < primary.Length; i++)
                    {
                        var idx = padded[i];
                        var arr = new int[area];
                        var baseOff = i * area;
                        for (int j = 0; j < area; j++) arr[j] = (int)src[baseOff + j];
                        var ch = world.GetOrCreateChunkContainer(idx);
                        ch.ApplyColumnHeightsForCollision(arr);
                        ch.HasGpuColumns = true;
                        ch.Visible = true;
                    }
                }
            }
        }
        catch { }

        // Stage G2a: count only primaries (build remap primary->padded)
        var map = new Dictionary<int, int>(padded.Length);
        for (int i = 0; i < padded.Length; i++) map[padded[i]] = i;
        var remap = new int[primary.Length];
        for (int i = 0; i < primary.Length; i++) remap[i] = map.TryGetValue(primary[i], out var gi) ? gi : 0;
        EnsureRemapBuffer(remap);
        var counts = RunCompactCount(padded, 0, primary.Length, remap);

        // Compute append bases using current resident total
        var oldIndices = world.CompactedChunkIndices ?? Array.Empty<int>();
        var oldBases = world.CompactedBases ?? Array.Empty<int>();
        var oldCounts = world.CompactedCounts ?? Array.Empty<int>();
        int oldTotal = 0; for (int i = 0; i < oldCounts.Length; i++) oldTotal += oldCounts[i];

        var bases = new int[primary.Length];
        int addTotal = 0; for (int i = 0; i < primary.Length; i++) { bases[i] = oldTotal + addTotal; addTotal += counts[i]; }
        if (addTotal <= 0)
        {
            // nothing to append
            return false;
        }

        // Stage G2b: allocate new atlas = oldTotal + addTotal; copy old then write new slice
        uint newAtlas;
        GL.CreateBuffers(1, out newAtlas);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, newAtlas, -1, "compactedAtlas_SSBO");
        int elemSize = 16; // sizeof(GpuBlockState)
        GL.NamedBufferStorage(newAtlas, Math.Max(1, oldTotal + addTotal) * elemSize, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        if (world.CompactedAtlasSSBO != 0 && oldTotal > 0)
        {
            // copy old content
            GL.CopyNamedBufferSubData(world.CompactedAtlasSSBO, newAtlas, IntPtr.Zero, IntPtr.Zero, (IntPtr)(oldTotal * elemSize));
        }

        // Bind new atlas for write pass
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 8, newAtlas);
        // Bind column heights and break mask similar to ProcessChunkData
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);
        EnsureCountsBuffer(primary.Length * sizeof(int));
        // Zero cursors (countsBuffer) via compute to avoid pixel-path sync
        var clearCursors = new Shader("Shaders/compute-clear-int-array.comp", ShaderType.ComputeShader);
        clearCursors.Use();
        clearCursors.SetInt("elementCount", primary.Length);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, countsBuffer);
        GL.DispatchCompute((primary.Length + 63) / 64, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 9, countsBuffer);
        FillBreakMask3D(primary, 0, primary.Length, remap);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, breakMask3DSSBO);

        // Upload append bases
        int basesSSBO;
        GL.CreateBuffers(1, out basesSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, basesSSBO, -1, "basesSSBO_append");
        GL.NamedBufferStorage(basesSSBO, bases.Length * sizeof(int), IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        GL.NamedBufferSubData(basesSSBO, IntPtr.Zero, bases.Length * sizeof(int), bases);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, basesSSBO);

        shaderCompactWrite.Use();
        shaderCompactWrite.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shaderCompactWrite.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        shaderCompactWrite.SetInt("waterBlockType", (int)BlockType.WaterLevel);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, blockAttribSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 13, remapSSBO);
        GL.DispatchCompute(primary.Length, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);

        // Build new arrays by concatenation
        var newIndices = new int[oldIndices.Length + primary.Length];
        var newBases = new int[oldBases.Length + bases.Length];
        var newCounts = new int[oldCounts.Length + counts.Length];
        if (oldIndices.Length > 0) Array.Copy(oldIndices, 0, newIndices, 0, oldIndices.Length);
        if (oldBases.Length > 0) Array.Copy(oldBases, 0, newBases, 0, oldBases.Length);
        if (oldCounts.Length > 0) Array.Copy(oldCounts, 0, newCounts, 0, oldCounts.Length);
        Array.Copy(primary, 0, newIndices, oldIndices.Length, primary.Length);
        Array.Copy(bases, 0, newBases, oldBases.Length, bases.Length);
        Array.Copy(counts, 0, newCounts, oldCounts.Length, counts.Length);

        // Publish pending: keep resident atlas alive until renderer swaps pending
        var sync = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        world.SetPendingCompaction(newAtlas, newIndices, newBases, newCounts, sync);
        return true;
    }

    public bool SubmitBatch(int[] chunkIndices)
    {
        if (chunkIndices == null || chunkIndices.Length == 0) return false;
        if (batchInFlight) return false;

        // Build padded batch: primary first, then unique 4-neighbors so border AO/visibility has neighbor data
        var primary = chunkIndices;
        var set = new HashSet<int>(primary);
        var paddedList = new List<int>(primary.Length * 2);
        paddedList.AddRange(primary);
        var side = VoxelHelper.WorldChunksXZ;
        foreach (var idx in primary)
        {
            var x = idx % side; var z = idx / side;
            int west = (x > 0) ? idx - 1 : -1;
            int east = (x + 1 < side) ? idx + 1 : -1;
            int north = (z > 0) ? idx - side : -1;
            int south = (z + 1 < side) ? idx + side : -1;
            if (west >= 0 && set.Add(west)) paddedList.Add(west);
            if (east >= 0 && set.Add(east)) paddedList.Add(east);
            if (north >= 0 && set.Add(north)) paddedList.Add(north);
            if (south >= 0 && set.Add(south)) paddedList.Add(south);
        }
        var padded = paddedList.ToArray();

        var chunkCount = primary.Length;
        var paddedCount = padded.Length;

        // Ensure previous GPU work finished before reusing SSBOs
        WaitForPreviousGpu();
        EnsureChunkIndicesCapacity(paddedCount);
        // Upload indices via unsynchronized mapped range to avoid pixel-path sync
        {
            var bytes = paddedCount * sizeof(int);
            var mapped = GL.MapNamedBufferRange(chunkIndicesSSBO, IntPtr.Zero, bytes,
                BufferAccessMask.MapWriteBit | BufferAccessMask.MapInvalidateBufferBit | BufferAccessMask.MapUnsynchronizedBit);
            if (mapped != IntPtr.Zero && paddedCount > 0)
            {
                unsafe
                {
                    fixed (int* src = &padded[0])
                    {
                        System.Buffer.MemoryCopy(src, (void*)mapped, (long)bytes, (long)bytes);
                    }
                }
                GL.UnmapNamedBuffer(chunkIndicesSSBO);
            }
            else
            {
                GL.NamedBufferSubData(chunkIndicesSSBO, IntPtr.Zero, bytes, padded);
            }
        }

        // Height SSBO not required; compute-chunk generates heights on GPU

        var bytesPerChunk_local = VoxelsCount * Unsafe.SizeOf<uint>();
        EnsureBlockTypeCapacity(bytesPerChunk_local * paddedCount);
        EnsureBlockAttribCapacity(bytesPerChunk_local * paddedCount);
        EnsureColumnHeightsOutputCapacity(paddedCount);

        // Bind and dispatch Pass 1 (types + initial AO/visibility)
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkIndicesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, blockAttribSSBO);
        shader.Use();
        shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shader.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        shader.SetInt("waterLevel", VoxelHelper.WaterLevel);
        shader.SetInt("worldChunksXZ", VoxelHelper.WorldChunksXZ);
        // GPU-side height generation params (approximate TerrainBuilder)
        shader.SetFloat("baseFreq", 1f / 180f);
        shader.SetFloat("warpFreq", 1f / 900f);
        shader.SetFloat("warpAmp", 8f);
        shader.SetInt("noiseSeed", world.Seed ^ 0x12345);
        // Upload/edit mask for Pass1 (compute-chunk applies breaks inline)
        FillBreakMask3D(padded, 0, paddedCount, null);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, breakMask3DSSBO);
        Log.CheckGlError(nameof(SubmitBatch) + " before DispatchCompute");

        GL.DispatchCompute(paddedCount, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        Log.CheckGlError(nameof(SubmitBatch) + " after Pass1");

        // Pass 2: GPU border AO/visibility fix
        EnsureNeighborGroups(padded, 0, paddedCount);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, neighborGroupsSSBO);
        shaderBorders.Use();
        shaderBorders.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shaderBorders.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        GL.DispatchCompute(paddedCount, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        Log.CheckGlError(nameof(SubmitBatch) + " after Pass2");

        // Throttled diagnostics (avoid flooding console)
        var nowMsDiag = System.Environment.TickCount64;
        // diagnostics removed

        // Stage 2: compact visible voxels into atlas and publish result for renderer
        // Build remap (primary -> index in padded buffer order)
        var map = new Dictionary<int, int>(paddedCount);
        for (int i = 0; i < paddedCount; i++) map[padded[i]] = i;
        var remap = new int[chunkCount];
        for (int i = 0; i < chunkCount; i++)
        {
            if (!map.TryGetValue(primary[i], out var gi) || gi < 0 || gi >= paddedCount)
            {
                gi = 0; // safe default to keep within range
            }
            remap[i] = gi;
        }
        EnsureRemapBuffer(remap);
        int[] counts = RunCompactCount(padded, 0, chunkCount, remap); // compaction reads via remap
        int[] bases = new int[chunkCount];
        long total64 = 0;
        for (int i = 0; i < chunkCount; i++) { bases[i] = (int)total64; total64 += counts[i]; }
        // Guard against pathological totals (e.g., bogus counts): skip publish if unreasonable
        var maxExpected = (long)VoxelsCount * chunkCount;
        if (total64 < 0 || total64 > maxExpected)
        {
            try { Log.Warn($"Compaction total out of range: total={total64}, expectedMax={maxExpected}. Skipping publish."); } catch { }
            // Mark batch complete without publishing; periodic full rebuild will correct atlas
            batchFence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            batchIndices = primary;
            batchCount = chunkCount;
            batchInFlight = true;
            return true;
        }
        int total = (int)total64;
        if (total <= 0)
        {
            // Do not replace the resident atlas with an empty publish
            try { Log.Warn("Compaction total (ProcessChunkData) is zero; skipping publish"); } catch { }
        }
        // diagnostics removed

        // Replace the previous atlas/draws each publish; no accumulation to avoid unbounded growth.
        // Heuristic: only accept publishes that are reasonably complete vs. expected surrounding size.
        // Otherwise skip publish and rely on the frequent full rebuild to update coherently.
        var expected = (2 * VoxelHelper.MaxDistanceInChunks + 1);
        expected *= expected;
        var minAccept = Math.Max(64, expected / 2);
        if (chunkCount < minAccept && world.CompactedAtlasSSBO != 0)
        {
            ;// Avoid replacing resident atlas with small/partial publish
        }
        else
        {
            var newAtlas = RunCompactWrite(primary, 0, chunkCount, bases, total, out var _);
            var newIndices = (int[])primary.Clone();
            var newBases = bases;
            var newCounts = counts; // use counts from count pass to match bases
            // Keep resident atlas alive; renderer swaps pending safely
            var sync = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            world.SetPendingCompaction(newAtlas, newIndices, newBases, newCounts, sync);
        }

        batchFence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        batchIndices = primary;
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

        // Apply CPU results (disabled for GPU-first rendering to avoid stalls)
        var indices = batchIndices;
        var count = batchCount;
        batchIndices = null;
        batchCount = 0;
        batchInFlight = false;

        // Skip ApplyBatch to avoid heavy CPU stall; renderer uses GPU compaction results.
        completed = indices;
        return true;
    }

    private unsafe void ApplyBatch(int[] chunkIndices, int start, int count)
    {
        var bytesPerChunk_local = VoxelsCount * Unsafe.SizeOf<uint>();
        var basePtr = (byte*)blockTypePtr;
        var columnArea = VoxelHelper.ChunkSideSizeSquare;
        var columnPtr = (byte*)columnHeightsPtr;
        var attribPtr = (byte*)blockAttribPtr;

        // Keep CPU work small; reuse arrays where possible would be ideal, but keep simple for now.
        for (var i = 0; i < count; i++)
        {
            var srcU32 = (uint*)(basePtr + i * bytesPerChunk_local);
            var srcHeights = (uint*)(columnPtr + i * columnArea * sizeof(uint));
            var srcAttribs = (uint*)(attribPtr + i * bytesPerChunk_local);
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
            // Allow mapping for write to avoid pixel-path sync during uploads
            var flags = BufferStorageFlags.DynamicStorageBit | BufferStorageFlags.MapWriteBit;
            GL.NamedBufferStorage(chunkIndicesSSBO, count * sizeof(int), IntPtr.Zero, flags);
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
            if (current >= requiredBytes) return;

            // drop old buffer if size/mapping mismatches
            GL.DeleteBuffer(blockTypeSSBO);
            blockTypeCapacityBytes = 0;
        }

        GL.CreateBuffers(1, out blockTypeSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, blockTypeSSBO, -1, "blockTypeSSBO");
        // GPU-only storage; we do not map block types on CPU
        GL.NamedBufferStorage(blockTypeSSBO, requiredBytes, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        blockTypePtr = IntPtr.Zero;
        blockTypeCapacityBytes = requiredBytes;
    }

    private unsafe void EnsureBlockAttribCapacity(int requiredBytes)
    {
        if (blockAttribSSBO != 0)
        {
            GL.GetNamedBufferParameter(blockAttribSSBO, BufferParameterName.BufferSize, out int current);
            if (current >= requiredBytes) return;

            GL.DeleteBuffer(blockAttribSSBO);
            blockAttribCapacityBytes = 0;
        }

        GL.CreateBuffers(1, out blockAttribSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, blockAttribSSBO, -1, "blockAttribSSBO");
        // GPU-only storage; we do not map attributes on CPU
        GL.NamedBufferStorage(blockAttribSSBO, requiredBytes, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        blockAttribPtr = IntPtr.Zero;
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
        if (count <= 0) return;
        var sw = Stopwatch.StartNew();

        // bind common SSBOs
        if (/*heightSSBO == 0 ||*/ blockTypeSSBO == 0 || chunkIndicesSSBO == 0 || columnHeightsSSBO == 0 || blockAttribSSBO == 0)
        {
            try {
                Log.Warn($"DispatchAndReadback: missing SSBO(s): height={heightSSBO}, types={blockTypeSSBO}, indices={chunkIndicesSSBO}, cols={columnHeightsSSBO}, attrib={blockAttribSSBO}. Skipping pass.");
            } catch { }
            return;
        }
        // Height SSBO not used by compute-chunk; heights generated on GPU
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkIndicesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, blockAttribSSBO);

        // Upload/edit mask for Pass1 (compute-chunk applies breaks inline)
        FillBreakMask3D(chunkIndices, start, count, null);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, breakMask3DSSBO);

        // uniforms
        shader.Use();
        shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shader.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        shader.SetInt("waterLevel", VoxelHelper.WaterLevel);
        shader.SetInt("worldChunksXZ", VoxelHelper.WorldChunksXZ);
        // GPU-side height generation params (approximate TerrainBuilder)
        shader.SetFloat("baseFreq", 1f / 180f);
        shader.SetFloat("warpFreq", 1f / 900f);
        shader.SetFloat("warpAmp", 8f);
        shader.SetInt("noiseSeed", world.Seed ^ 0x12345);
        // Bind persistent break mask so compute-chunk can apply edits inline
        FillBreakMask3D(chunkIndices, start, count, null);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, breakMask3DSSBO);

        // Pass 1: types + initial AO/visibility
        GL.DispatchCompute(count, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        Log.CheckGlError(nameof(DispatchAndReadback) + " after Pass1");

        // Pass 2: GPU border AO/visibility fix using neighbor blocktypes
        EnsureNeighborGroups(chunkIndices, start, count);
        if (neighborGroupsSSBO == 0)
        {
            try { Log.Warn("DispatchAndReadback: neighborGroupsSSBO not initialized, skipping Pass2"); } catch { }
        }
        else
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, neighborGroupsSSBO);
            shaderBorders.Use();
            shaderBorders.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
            shaderBorders.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
            GL.DispatchCompute(count, 1, 1);
        }
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        Log.CheckGlError(nameof(DispatchAndReadback) + " after Pass2");

        // Ensure GPU writes to persistently mapped buffers are visible to CPU before reading
        var readSync = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        var waitStatus = GL.ClientWaitSync(readSync, ClientWaitSyncFlags.SyncFlushCommandsBit, 50_000_000);
        try { GL.DeleteSync(readSync); } catch { }
        sw.Restart();

        // No CPU-side memcpy here in GPU-first path; all data stays on GPU
        // Throttle noisy timing log to avoid console flood
        // timing log removed

        // --- Stage 2: GPU compaction of visible voxels into atlas ---
        // Identity remap (no padding in this path)
        var nowTick = System.Environment.TickCount64;
        var remapId = new int[count]; for (int i = 0; i < count; i++) remapId[i] = i;
        EnsureRemapBuffer(remapId);
        // Count first to size atlas precisely
        var counts = RunCompactCount(chunkIndices, start, count, remapId);
        try {
            if (world.Camera is not null)
            {
                var camPos = world.Camera.Position;
                var cx = (int)((camPos.X + 0.5f) / VoxelHelper.ChunkSideSize);
                var cz = (int)((camPos.Z + 0.5f) / VoxelHelper.ChunkSideSize);
                var camIdx = cx + cz * VoxelHelper.WorldChunksXZ;
                var idxIn = Array.IndexOf(chunkIndices, camIdx, start, count);
                if (idxIn >= 0)
                {
                    Log.Info($"GPU Count (pre-write): cam draw={idxIn - start} count={counts[idxIn - start]}");
                }
            }
        } catch { }
        try
        {
            long preTotal = 0; for (int i = 0; i < count; i++) preTotal += counts[i];
            var sample = string.Join(" ", counts.Take(Math.Min(4, count)).Select((v,i)=>$"c{i}={v}"));
            Log.Info($"GPU Count: draws={count}, sum={preTotal} [{sample}]");
        }
        catch { }
        bool allZero = true; for (int i = 0; i < counts.Length; i++) if (counts[i] != 0) { allZero = false; break; }
        if (allZero) { for (int i = 0; i < count; i++) counts[i] = VoxelHelper.ChunkSideSizeSquare; }
        var bases = new int[count];
        long total64 = 0;
        for (int i = 0; i < count; i++) { bases[i] = (int)total64; total64 += counts[i]; }
        var total = (int)total64;
        // Sanity: avoid replacing resident atlas with implausibly small totals
        if (total <= 0 || total < count * 8)
        {
            try { Log.Warn($"GPU Count total too small (sync): total={total}, draws={count}; skipping write"); } catch { }
            return; // keep previous atlas
        }
        // simple debounce to avoid republishing multiple times per frame
        if (nowTick - lastPublishTick < 150) return;
        uint atlas = RunCompactWrite(chunkIndices, start, count, bases, total, out var _);
        if (total <= 0) { GL.DeleteBuffer(atlas); return; }
        var subset = new int[count];
        Array.Copy(chunkIndices, start, subset, 0, count);
        // Publish as pending with a fence for safe swap in renderer
        var sync2 = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        world.SetPendingCompaction(atlas, subset, bases, counts, sync2);
        try {
            Log.Info($"GPU Publish: draws={count}, totalInstances={total}");
            // Debug: sample mapping for camera chunk
            if (world.Camera is not null)
            {
                var camPos = world.Camera.Position;
                var cx = (int)((camPos.X + 0.5f) / VoxelHelper.ChunkSideSize);
                var cz = (int)((camPos.Z + 0.5f) / VoxelHelper.ChunkSideSize);
                var camIdx = cx + cz * VoxelHelper.WorldChunksXZ;
                var idxIn = Array.IndexOf(subset, camIdx);
                if (idxIn >= 0)
                {
                    int b = bases[idxIn]; int c = counts[idxIn];
                    Log.Info($"MDI map: camChunk={camIdx} draw={idxIn} base={b} count={c}");
                }
                else
                {
                    Log.Info($"MDI map: camChunk={camIdx} not in draw list");
                }
            }
        } catch { }
        // Block once on the very first publish to ensure atlas is ready for MainScene
        if (lastPublishTick == 0)
        {
            var r = GL.ClientWaitSync(sync2, ClientWaitSyncFlags.SyncFlushCommandsBit, 50_000_000);
            if (r == WaitSyncStatus.AlreadySignaled || r == WaitSyncStatus.ConditionSatisfied)
            {
                try { world.TrySwapPendingCompaction(); } catch { }
            }
        }
        lastPublishTick = nowTick;
        // reduced logging

        // Update collision heights from GPU column heights (binding 3)
        try
        {
            unsafe
            {
                int area = VoxelHelper.ChunkSideSizeSquare;
                if (columnHeightsPtr != IntPtr.Zero)
                {
                    var src = (uint*)columnHeightsPtr;
                    for (int i = 0; i < count; i++)
                    {
                        var idx = chunkIndices[start + i];
                        var arr = new int[area];
                        var baseOff = i * area;
                        for (int j = 0; j < area; j++) arr[j] = (int)src[baseOff + j];
                        var ch = world.GetOrCreateChunkContainer(idx);
                        ch.ApplyColumnHeightsForCollision(arr);
                        ch.HasGpuColumns = true;
                        // GPU-first: mark chunk initialized/processed so systems relying on these flags behave consistently

                        ch.Visible = true;

                        // Build CPU Blocks[] from GPU columns so CPU systems have a coherent view

                    }
                }
            }
        }
        catch { }
    }

    private void EnsureNeighborGroups(int[] indices, int start, int count)
    {
        if (count <= 0) return;
        var requiredBytes = count * 4 * sizeof(int);
        if (neighborGroupsSSBO == 0)
        {
            GL.CreateBuffers(1, out neighborGroupsSSBO);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, neighborGroupsSSBO, -1, "neighborGroupsSSBO");
            neighborGroupsCapacity = 0;
        }
        if (requiredBytes > neighborGroupsCapacity)
        {
            GL.NamedBufferStorage(neighborGroupsSSBO, requiredBytes, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            neighborGroupsCapacity = requiredBytes;
        }
        var map = new Dictionary<int, int>(count);
        for (int i = 0; i < count; i++) map[indices[start + i]] = i;
        var data = new int[count * 4];
        var side = VoxelHelper.WorldChunksXZ;
        for (int i = 0; i < count; i++)
        {
            var worldIdx = indices[start + i];
            var x = worldIdx % side;
            var z = worldIdx / side;
            int west = (x > 0) ? worldIdx - 1 : -1;
            int east = (x + 1 < side) ? worldIdx + 1 : -1;
            int north = (z > 0) ? worldIdx - side : -1;
            int south = (z + 1 < side) ? worldIdx + side : -1;
            data[i * 4 + 0] = (west >= 0 && map.TryGetValue(west, out var gw)) ? gw : -1;
            data[i * 4 + 1] = (east >= 0 && map.TryGetValue(east, out var ge)) ? ge : -1;
            data[i * 4 + 2] = (north >= 0 && map.TryGetValue(north, out var gn)) ? gn : -1;
            data[i * 4 + 3] = (south >= 0 && map.TryGetValue(south, out var gs)) ? gs : -1;
        }
        GL.NamedBufferSubData(neighborGroupsSSBO, IntPtr.Zero, requiredBytes, data);
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

    private int[] RunCompactCount(int[] chunkIndices, int start, int count, int[] remapForMasks)
    {
        int bytes = count * sizeof(int);
        // Ensure a persistently mapped counts buffer and bind at 6
        EnsureCountsBuffer(bytes);
        // If for any reason EnsureCountsBuffer failed to create (driver quirk), create a minimal buffer now
        if (countsBuffer == 0 && bytes > 0)
        {
            GL.CreateBuffers(1, out countsBuffer);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, countsBuffer, -1, "countsSSBO");
            var flags = BufferStorageFlags.DynamicStorageBit; // fallback: no mapping, host readback only
            GL.NamedBufferStorage(countsBuffer, bytes, IntPtr.Zero, flags);
            countsPtr = IntPtr.Zero;
            countsCapacityBytes = bytes;
        }
        // Zero counts via compute to avoid pixel-path sync
        if (bytes > 0)
        {
            var clear = new Shader("Shaders/compute-clear-int-array.comp", ShaderType.ComputeShader);
            clear.Use();
            clear.SetInt("elementCount", count);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, countsBuffer);
            GL.DispatchCompute((count + 63) / 64, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        }
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, countsBuffer);

        shaderCompactCount.Use();
        // Bind inputs used by the shader
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);   // block types
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, blockAttribSSBO); // attributes
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 13, remapSSBO);      // primary->padded
        // Bind column heights and break mask to mirror compact-write logic
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);
        // Build/Bind a break mask 3D bitset (binding 16), 32 voxels per uint
        FillBreakMask3D(chunkIndices, start, count, remapForMasks);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, breakMask3DSSBO);
        shaderCompactCount.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shaderCompactCount.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        shaderCompactCount.SetInt("waterBlockType", (int)BlockType.WaterLevel);
        GL.DispatchCompute(count, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        // Ensure GPU has finished writing counts before CPU reads from the persistently mapped buffer
        var fenceCount = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        var waitCount = GL.ClientWaitSync(fenceCount, ClientWaitSyncFlags.SyncFlushCommandsBit, 50_000_000);
        try { GL.DeleteSync(fenceCount); } catch { }
        Log.CheckGlError(nameof(RunCompactCount) + " after Dispatch");

        // Copy from persistent map if available; otherwise fall back to GetNamedBufferSubData
        int[] counts = new int[count];
        if (count > 0)
        {
            if (countsPtr != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.Copy(countsPtr, counts, 0, count);
            }
            else
            {
                // Fallback: avoid CPU readback by copying into countsBuffer then Map-read once
                EnsureCountsBuffer(bytes);
                GL.CopyNamedBufferSubData(countsBuffer, countsBuffer, IntPtr.Zero, IntPtr.Zero, bytes);
                if (countsPtr != IntPtr.Zero)
                    System.Runtime.InteropServices.Marshal.Copy(countsPtr, counts, 0, count);
            }
            // Sanity clamp to expected range [0, VoxelsCount]
            bool clamped = false;
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] < 0) { counts[i] = 0; clamped = true; }
                else if (counts[i] > VoxelsCount) { counts[i] = VoxelsCount; clamped = true; }
            }
            if (clamped)
            {
                try { Log.Warn("Compaction counts clamped to valid range (detected out-of-bounds values)"); } catch { }
            }
        }
        // Reuse persistent break mask buffer; do not delete
        return counts;
    }

    private uint RunCompactWrite(int[] chunkIndices, int start, int count, int[] bases, int total, out int[] countsOut, int[]? remapForMasks = null)
    {
        int basesBytes = count * sizeof(int);
        int basesSSBO;
        GL.CreateBuffers(1, out basesSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, basesSSBO, -1, "basesSSBO");
        GL.NamedBufferStorage(basesSSBO, basesBytes, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        GL.NamedBufferSubData(basesSSBO, IntPtr.Zero, basesBytes, bases);

        // Ensure a counts/cursors buffer exists (persistently mapped). We won't read it on CPU.
        EnsureCountsBuffer(basesBytes);
        // Zero cursors (countsBuffer) via compute to avoid pixel-path sync
        var clear2 = new Shader("Shaders/compute-clear-int-array.comp", ShaderType.ComputeShader);
        clear2.Use();
        clear2.SetInt("elementCount", count);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, countsBuffer);
        GL.DispatchCompute((count + 63) / 64, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);

        uint atlasSSBO;
        GL.CreateBuffers(1, out atlasSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, atlasSSBO, -1, "compactedAtlas_SSBO");
        int elemSize = 16; // std430 array-of-struct stride for 3x4-byte fields
        GL.NamedBufferStorage(atlasSSBO, System.Math.Max(total,1) * elemSize, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);

        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, basesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 8, atlasSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 9, countsBuffer);
        // Also bind column heights and optional break mask for stable top-surface write
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);
        // Build/Bind break mask buffer (binding 16)
        int area = VoxelHelper.ChunkSideSizeSquare;
        int wordsPerChunk = ((area * VoxelHelper.ChunkYSize) + 31) / 32;
        int maskBytes = count * wordsPerChunk * sizeof(uint);
        EnsureBreakMask3DCapacity(count);
        unsafe {
            var zero = new uint[] { 0u };
            for (int i = 0; i < count; i++)
            {
                var idx = chunkIndices[start + i];
                var mask = world.GetBreakMask3D(idx);
                if (mask is null)
                {
                    int mapped = (remapForMasks != null && remapForMasks.Length == count) ? remapForMasks[i] : i;
                    GL.ClearNamedBufferSubData(breakMask3DSSBO, PixelInternalFormat.R32ui, (IntPtr)(mapped * wordsPerChunk * sizeof(uint)), (IntPtr)(wordsPerChunk * sizeof(uint)), PixelFormat.RedInteger, PixelType.UnsignedInt, zero);
                }
                else
                {
                    int mapped = (remapForMasks != null && remapForMasks.Length == count) ? remapForMasks[i] : i;
                    fixed (byte* p = &mask[0])
                        GL.NamedBufferSubData(breakMask3DSSBO, (IntPtr)(mapped * wordsPerChunk * sizeof(uint)), wordsPerChunk * sizeof(uint), (IntPtr)p);
                }
            }
        }
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 16, breakMask3DSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, columnHeightsSSBO);

        shaderCompactWrite.Use();
        shaderCompactWrite.SetInt("chunkSize", VoxelHelper.ChunkSideSize);
        shaderCompactWrite.SetInt("chunkYSize", VoxelHelper.ChunkYSize);
        shaderCompactWrite.SetInt("waterBlockType", (int)BlockType.WaterLevel);
        // Bind inputs/outputs used by the shader
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);   // block types
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, blockAttribSSBO); // attributes
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 13, remapSSBO);      // primary->padded
        GL.DispatchCompute(count, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
        // Ensure GPU cursors are fully written before CPU reads back per-chunk counts
        var fenceWrite = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        var waitWrite = GL.ClientWaitSync(fenceWrite, ClientWaitSyncFlags.SyncFlushCommandsBit, 50_000_000);
        try { GL.DeleteSync(fenceWrite); } catch { }
        Log.CheckGlError(nameof(RunCompactWrite) + " after Dispatch");

        // Read back exact per-chunk cursors from persistent mapped counts buffer
        countsOut = new int[count];
        if (count > 0 && countsPtr != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.Copy(countsPtr, countsOut, 0, count);
        }

        GL.DeleteBuffer(basesSSBO);
        return atlasSSBO;
    }

    // Removed RunCompactWriteAppend: append path replaced by single-pass publish using persistent mapped buffers
    private void EnsureCountsBuffer(int requiredBytes)
    {
        if (countsBuffer != 0)
        {
            int current; GL.GetNamedBufferParameter(countsBuffer, BufferParameterName.BufferSize, out current);
            if (current >= requiredBytes && countsPtr != IntPtr.Zero) return;
            GL.DeleteBuffer(countsBuffer); countsBuffer = 0; countsPtr = IntPtr.Zero; countsCapacityBytes = 0;
        }
        GL.CreateBuffers(1, out countsBuffer);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, countsBuffer, -1, "countsSSBO");
        var flags = BufferStorageFlags.MapReadBit | BufferStorageFlags.MapPersistentBit | BufferStorageFlags.MapCoherentBit | BufferStorageFlags.DynamicStorageBit;
        GL.NamedBufferStorage(countsBuffer, requiredBytes, IntPtr.Zero, flags);
        countsPtr = GL.MapNamedBufferRange(countsBuffer, IntPtr.Zero, requiredBytes,
            BufferAccessMask.MapReadBit | BufferAccessMask.MapPersistentBit | BufferAccessMask.MapCoherentBit);
        countsCapacityBytes = requiredBytes;
    }

    private void EnsureRemapBuffer(int[] remap)
    {
        var requiredBytes = remap.Length * sizeof(int);
        if (remapSSBO == 0)
        {
            GL.CreateBuffers(1, out remapSSBO);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, remapSSBO, -1, "primaryToPaddedSSBO");
            remapCapacityBytes = 0;
        }
        int current; GL.GetNamedBufferParameter(remapSSBO, BufferParameterName.BufferSize, out current);
        if (current < requiredBytes)
        {
            if (current > 0) GL.DeleteBuffer(remapSSBO);
            GL.CreateBuffers(1, out remapSSBO);
            GL.ObjectLabel(ObjectLabelIdentifier.Buffer, remapSSBO, -1, "primaryToPaddedSSBO");
            GL.NamedBufferStorage(remapSSBO, requiredBytes, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
            remapCapacityBytes = requiredBytes;
        }
        GL.NamedBufferSubData(remapSSBO, IntPtr.Zero, requiredBytes, remap);
    }
}



























