using OpenRender;
using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Culling;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SpyroGame.World;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace SpyroGame.Components;

internal class ChunkRenderer : SceneNode
{
    private static readonly Vector3 ChunkPositionOffset = new(
        (VoxelHelper.ChunkSideSize / 2f) - 0.5f,
        (VoxelHelper.ChunkYSize / 2f) - 0.5f,
        (VoxelHelper.ChunkSideSize / 2f) - 0.5f
    );

    /// <summary>
    /// Default number <cref>BlockState</cref> instance structure for which SSBO buffer memory is allocated.
    /// </summary>
    private static readonly int DefaultMaxInstances = (VoxelHelper.ChunkSideSize * VoxelHelper.ChunkSideSize * VoxelHelper.ChunkYSize) / 8;

    private readonly uint texturesSSBO;
    private readonly uint materialsSSBO;
    //private readonly uint sharedBlocksSSBO;

    private readonly List<ChunkRenderData> renderDataList = [];
    private readonly VoxelWorld world;

    private IEnumerable<ChunkRenderData> sortedVisibleChunkList = [];

    internal ConcurrentQueue<Chunk> initializedChunksQueue = [];

    //private IndexBuffer indexBuffer;
    //private Buffer<float> vertexBuffer;
    //private VertexArrayObject vao;

    public ChunkRenderer(VoxelWorld world, Mesh mesh, Material material, ulong[] textureHandles, VoxelMaterial[] materials) : base(mesh, material)
    {
        this.world = world;

        foreach (var handle in textureHandles)
        {
            Texture.MakeResident(handle);
        }

        //  prepare textures and materials SSBOs
        GL.CreateBuffers(1, out texturesSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, texturesSSBO, -1, "voxelTextures_SSBO");
        GL.NamedBufferStorage(texturesSSBO, textureHandles.Length * Unsafe.SizeOf<ulong>(), textureHandles, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.CreateBuffers(1, out materialsSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, materialsSSBO, -1, "voxelMaterials_SSBO");
        GL.NamedBufferStorage(materialsSSBO, materials.Length * Unsafe.SizeOf<VoxelMaterial>(), materials, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);

        ////  index & vertex buffer are shared between all VAOs
        //vertexBuffer = new Buffer<float>(Mesh.Vertices);
        //indexBuffer = new IndexBuffer(Mesh.Indices);
        //vao = new VertexArrayObject();
        //vao.AddBuffer(Mesh.VertexDeclaration, vertexBuffer);
        //vao.AddIndexBuffer(indexBuffer);

        Log.CheckGlError();

        RenderGroup = RenderGroup.SkyBox;
        DisableCulling = true;
    }

    /// <summary>
    /// Total number of chunks passing the culling test.
    /// </summary>
    public int ChunksInFrustum { get; private set; }
        
    /// <summary>
    /// Total number of blocks rendered.
    /// </summary>
    public int RenderedBlocks { get; private set; }

    public override void OnDraw(double elapsed)
    {
        GL.BindVertexArray(Vao!);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, texturesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, materialsSSBO);
        Material.Shader.SetInt("chunkSize", VoxelHelper.ChunkSideSize);

        RenderedBlocks = 0;
        foreach (var chunkData in sortedVisibleChunkList)
        {
            RenderedBlocks += chunkData.SolidCount;

            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkData.BlocksSSBO);
            if (!ShowBoundingSphere)
            {
                transform.worldMatrix.Row3.Xyz = chunkData.Position;
                Material.Shader.SetMatrix4("model", ref transform.worldMatrix);
                GL.DrawElementsInstanced(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0, chunkData.SolidCount);

                if(chunkData.TransparentCount > 0)
                { 
                    GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkData.TransparentBlocksSSBO);
                    GL.DrawElementsInstanced(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0, chunkData.TransparentCount);
                }
            }
            else
            {
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                transform.worldMatrix.Row3.Xyz = chunkData.Position;
                Material.Shader.SetMatrix4("model", ref transform.worldMatrix);
                //GL.DrawElementsInstanced(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0, chunkData.Count);

                GL.Disable(EnableCap.CullFace);
                transform.worldMatrix.Row3.Xyz = chunkData.Position + ChunkPositionOffset;
                Matrix4.CreateScale(VoxelHelper.ChunkSideSize, VoxelHelper.ChunkYSize, VoxelHelper.ChunkSideSize, out var scaleMatrix);
                var worldMatrix = scaleMatrix * transform.worldMatrix;
                Scene!.DefaultShader.Use();
                Scene.DefaultShader.SetMatrix4("model", ref worldMatrix);

                GL.DrawElements(PrimitiveType.Triangles, Vao!.DataLength, DrawElementsType.UnsignedInt, 0);
            }
            GL.Enable(EnableCap.CullFace);
        }
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        if (initializedChunksQueue.TryDequeue(out var chunk))
        {
            var old = renderDataList.Find(x => x.Index == chunk.Index);
            if (old != null)
            {
                renderDataList.Remove(old);
                GL.DeleteBuffer(old.BlocksSSBO);
                GL.DeleteBuffer(old.TransparentBlocksSSBO);
                Log.CheckGlError();
            }
            renderDataList.Add(CreateChunkRenderData(chunk));
            if(!initializedChunksQueue.IsEmpty)
            {
                Log.Debug($"initializedChunksQueue items: {initializedChunksQueue.Count}");
            }
            Scene!.Camera?.Invalidate();
        }

        if (Scene!.Camera?.IsDirty ?? false)
        {
            var cullCandidates = renderDataList.Where(x => world.SurroundingChunkIndices.Contains(x.Index)).ToArray();
            foreach (var chunkRenderData in cullCandidates)
            {
                var visible = CullingHelper.IsAABBInFrustum(chunkRenderData.Aabb, Scene.Renderer.Frustum.Planes);
                chunkRenderData.Visible = visible;
            }
            sortedVisibleChunkList = cullCandidates
                .Where(x => x.Visible)
                .OrderBy(x => Vector3.DistanceSquared(x.Position + VoxelWorld.ChunkHalfSize, Scene.Camera.Position))
                .ToArray();
            ChunksInFrustum = sortedVisibleChunkList.Count();
        }
    }

    private ChunkRenderData CreateChunkRenderData(Chunk chunk)
    {
        //  solid blocks
        var blockData = chunk.VisibleBlocks.ToArray();
        var maxInstances = DefaultMaxInstances > blockData.Length ? DefaultMaxInstances : blockData.Length;
        //  prepare block data SSBOs
        GL.CreateBuffers(1, out uint blocksSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, blocksSSBO, -1, $"blocks_Chunk_{chunk}_SSBO");
        GL.NamedBufferStorage(blocksSSBO, maxInstances * Unsafe.SizeOf<BlockState>(), 0, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.NamedBufferSubData(blocksSSBO, 0, blockData.Length * Unsafe.SizeOf<BlockState>(), blockData);

        //  transparent blocks
        var transparentBlockData = chunk.TransparentBlocks.ToArray();
        maxInstances = DefaultMaxInstances > transparentBlockData.Length ? DefaultMaxInstances : transparentBlockData.Length;
        //  prepare block data SSBOs
        GL.CreateBuffers(1, out uint transparentBlocksSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, transparentBlocksSSBO, -1, $"transparentBlocks_Chunk_{chunk}_SSBO");
        GL.NamedBufferStorage(transparentBlocksSSBO, maxInstances * Unsafe.SizeOf<BlockState>(), 0, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.NamedBufferSubData(transparentBlocksSSBO, 0, transparentBlockData.Length * Unsafe.SizeOf<BlockState>(), transparentBlockData);
        Log.CheckGlError();

        ChunkRenderData chunkData = new(
           blocksSSBO,
           blockData.Length,
           transparentBlocksSSBO,
           transparentBlockData.Length,
           chunk.Aabb,
           chunk.Index);
        return chunkData;
    }
}

/// <summary>
/// Holds data for rendering a chunk.
/// </summary>
/// <param name="BlocksSSBO">Handle to SSBO buffer</param>
/// <param name="SolidCount">Number of BlockData structures in the BlocksSSBO buffer</param>
/// <param name="TransparentBlocksSSBO">Handle to SSBO buffer</param>
/// <param name="TransparentCount">Number of BlockData structures in the BlocksSSBO buffer</param>
/// <param name="Aabb">Axis aligned bounding box of the chunk</param>
internal record ChunkRenderData(uint BlocksSSBO, int SolidCount, uint TransparentBlocksSSBO, int TransparentCount, AABB Aabb, int Index)
{
    public bool Visible { get; set; }
    public Vector3i Position => Aabb.min;
}