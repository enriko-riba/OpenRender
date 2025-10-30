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

    // persistent map for blockTypeSSBO
    private IntPtr blockTypePtr = IntPtr.Zero;
    private int blockTypeCapacityBytes;
    private int chunkIndicesCapacity;  // ints allocated in chunkIndicesSSBO
    private int heightCapacityFloats;

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

        var chunkCount = chunkIndices.Length;

        EnsureChunkIndicesCapacity(chunkCount);
        GL.NamedBufferSubData(chunkIndicesSSBO, 0, chunkCount * sizeof(int), chunkIndices);

        EnsureHeightCapacity(chunkCount);
        UploadHeights(chunkIndices);

        var bytesPerChunk = VoxelsCount * Unsafe.SizeOf<uint>();
        EnsureBlockTypeCapacity(bytesPerChunk * chunkCount);

        DispatchAndReadback(chunkIndices, 0, chunkCount);
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


    private void DispatchAndReadback(int[] chunkIndices, int start, int count)
    {
        var sw = Stopwatch.StartNew();

        // bind common SSBOs
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, heightSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, blockTypeSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkIndicesSSBO);

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
            // readback from persistent map (no driver copy)
            var bytesPerChunk = VoxelsCount * Unsafe.SizeOf<uint>();
            var basePtr = (byte*)blockTypePtr;

            Parallel.For(0, count, i =>
            {
                var srcU32 = (uint*)(basePtr + i * bytesPerChunk);
                var worldChunkIndex = chunkIndices[start + i];
                var chunk = world.CreateChunk(worldChunkIndex);
                for (var v = 0; v < VoxelsCount; v++)
                {
                    ref var b = ref chunk.Blocks[v];
                    b.BlockType = (BlockType)srcU32[v];
                }
            });
        }
        Log.Info($"DispatchAndReadback memcopy time: {sw.ElapsedMilliseconds:N2} ms");
        
        sw.Restart();
        Parallel.For(0, count, i =>
        {
            var worldChunkIndex = chunkIndices[start + i];
            var chunk = world[worldChunkIndex];
            chunk?.CalcVisibleBlocks();
        });
        Log.CheckGlError(nameof(DispatchAndReadback) + " after readback");
        Log.Info($"DispatchAndReadback CalcVisibleBlocks time: {sw.ElapsedMilliseconds:N2} ms");
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
        //  TODO: implement shader.Dispose();
    }
}
