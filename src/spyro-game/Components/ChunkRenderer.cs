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
using System.Runtime.CompilerServices;

namespace SpyroGame.Components;

internal class ChunkRenderer : SceneNode
{
    private readonly uint texturesSSBO;
    private readonly uint materialsSSBO;
    private readonly List<ChunkRenderData> renderDataList = [];
    private readonly Mesh _mesh;

    private readonly Frustum frustum = new();
    private int visibleChunks;
    private int renderedBlocks;


    public ChunkRenderer(Mesh mesh, Material material, ulong[] textureHandles, VoxelMaterial[] materials) : base(mesh, material)
    {
        _mesh = mesh;
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

        //  upload texture handles and materials to GPU
        //GL.NamedBufferSubData(texturesSSBO, 0, textureHandles.Length * Unsafe.SizeOf<ulong>(), textureHandles);
        //GL.NamedBufferSubData(materialsSSBO, 0, materials.Length * Unsafe.SizeOf<VoxelMaterial>(), materials);
        Log.CheckGlError();

        RenderGroup = RenderGroup.SkyBox;
        DisableCulling = true;
    }

    public void AddChunkData(Chunk chunk, BlockState[] blockData)
    {
        renderDataList.Add(CreateChunkRenderData(chunk, blockData));
        //Log.Debug($"added chunk {chunk.Index}@{chunk.Position} with visible blocks {blockData.Length}, total chunks {renderDataList.Count}");
    }

    public bool IsChunkAdded(Vector3 position) => renderDataList.Any(x => x.Position == position);
    public int VisibleChunks => visibleChunks;
    public int RenderedBlocks => renderedBlocks;

    private static readonly Vector3 chunkPositionOffset = new ((Chunk.Size / 2f) - (0.5f));
    public override void OnDraw(double elapsed)
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, texturesSSBO);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, materialsSSBO);

        foreach (var chunkData in renderDataList)
        {
            if (chunkData.Visible == false || chunkData.Count == 0) continue;

            transform.worldMatrix.Row3.Xyz = chunkData.Position;
            Material.Shader.SetMatrix4("model", ref transform.worldMatrix);

            GL.BindVertexArray(chunkData.Vao!);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, chunkData.BlocksSSBO);

            if (!ShowBoundingSphere)
                GL.DrawElementsInstanced(PrimitiveType.Triangles, chunkData.Vao.DataLength, DrawElementsType.UnsignedInt, 0, chunkData.Count);
            else
            {
                //GL.Disable(EnableCap.CullFace);
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
                transform.worldMatrix.Row3.Xyz = chunkData.Position + chunkPositionOffset;
                Matrix4.CreateScale(Chunk.Size, out var scaleMatrix);
                var worldMatrix = scaleMatrix * transform.worldMatrix;
                Scene!.DefaultShader.Use();
                Scene.DefaultShader.SetMatrix4("model", ref worldMatrix);

                GL.DrawElements(PrimitiveType.Triangles, chunkData.Vao.DataLength, DrawElementsType.UnsignedInt, 0);
            }
            //GL.Enable(EnableCap.CullFace);
        }
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
    }

    public override void OnUpdate(Scene scene, double elapsed)
    {
        if (Scene?.Camera?.IsDirty ?? false)
        {
            visibleChunks = 0;
            renderedBlocks = 0;
            frustum.Update(Scene.Camera);
            foreach (var chunkRenderData in renderDataList)
            {
                var visible = CullingHelper.IsAABBInFrustum(chunkRenderData.Aabb, frustum.Planes);
                chunkRenderData.Visible = visible;
                if (visible)
                {
                    renderedBlocks += chunkRenderData.Count;
                    visibleChunks++;
                }
            }
        }
    }

    private ChunkRenderData CreateChunkRenderData(Chunk chunk, BlockState[] blockData)
    {
        var vao = new VertexArrayObject();
        vao.AddBuffer(_mesh.VertexDeclaration, new Buffer<float>(_mesh.Vertices));
        vao.AddIndexBuffer(new IndexBuffer(_mesh.Indices));

        var maxInstances = (Chunk.Size * Chunk.Size * Chunk.Size) / 8;
        maxInstances = maxInstances > blockData.Length ? maxInstances : blockData.Length;

        //  prepare block data SSBOs
        GL.CreateBuffers(1, out uint blocksSSBO);
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, blocksSSBO, -1, "blockdata_SSBO");
        GL.NamedBufferStorage(blocksSSBO, maxInstances * Unsafe.SizeOf<BlockState>(), 0, BufferStorageFlags.MapWriteBit | BufferStorageFlags.DynamicStorageBit);
        GL.NamedBufferSubData(blocksSSBO, 0, blockData.Length * Unsafe.SizeOf<BlockState>(), blockData);
        Log.CheckGlError();

        ChunkRenderData chunkData = new(
           vao,
           blocksSSBO,
           blockData.Length,
           chunk.Position,
           chunk.AABB);
        return chunkData;
    }
}

internal record ChunkRenderData(VertexArrayObject Vao, uint BlocksSSBO, int Count, Vector3i Position, AABB Aabb)
{
    public bool Visible { get; set; }
}