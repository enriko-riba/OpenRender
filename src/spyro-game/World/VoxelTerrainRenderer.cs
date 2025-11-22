using OpenRender;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Manages rendering of GPU-generated voxel terrain (Phase 4).
/// Consumes output from Phase 3 (compacted vertices and indices).
/// Integrates with the scene graph as a SceneNode.
/// </summary>
public class VoxelTerrainRenderer : SceneNode, IDisposable
{
    private uint vao;
    private Phase3BufferManager? bufferManager;
    private uint actualVertexCount;
    private uint actualFaceCount;           // Phase 5.1: Track face count instead
    private bool disposed;

    // Debug frame counter for logging
    private int frameCounter = 0;

    // Visibility tracking (for frustum culling)
    private int[]? visibilityFlags;
    private int[]? chunkIndices;

    // Block outline rendering
    private Shader? outlineShader;
    private uint outlineVao;
    private uint outlineVbo;
    private uint outlineEbo;
    private readonly Vector3 outlineColor = new(1.0f, 1.0f, 0.0f); // Yellow

    /// <summary>
    /// Number of visible chunks (after frustum culling)
    /// </summary>
    public int VisibleDraws { get; private set; }

    /// <summary>
    /// Total number of draw commands (Phase 5.3: one per chunk)
    /// </summary>
    public uint RenderedBlocks => actualFaceCount;  // Now stores command count, not face count

    /// <summary>
    /// Total draw call count (1 multi-draw indirect call)
    /// </summary>
    public int DrawCallCount => actualFaceCount > 0 ? 1 : 0;

    /// <summary>
    /// The currently picked/highlighted block (used for outlining/breaking).
    /// Set externally by game logic (e.g., raycasting).
    /// </summary>
    public BlockState? PickedBlock { get; set; }

    public VoxelTerrainRenderer() : base(CreateDummyMesh(), CreateDummyMaterial())
    {
        // Create VAO
        GL.CreateVertexArrays(1, out vao);
        GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, vao, -1, "voxel_terrain_vao");

        // Initialize outline rendering resources
        InitializeOutlineRendering();

        // Don't cull this node - we handle culling internally with GPU frustum culling
        DisableCulling = true;
        RenderGroup = RenderGroup.Default;

        Log.Info("VoxelTerrainRenderer: Initialized");
    }

    /// <summary>
    /// Factory method to create a VoxelTerrainRenderer.
    /// </summary>
    public static VoxelTerrainRenderer Create(VoxelWorld world, ulong[] textureHandles, VoxelMaterial[] materials)
    {
        // Create renderer - texture handles and materials will be used by ChunkStreamingManager
        // This renderer uses its own shader and material setup
        return new VoxelTerrainRenderer();
    }

    /// <summary>
    /// Set up VAO to use Phase 3 compacted buffers.
    /// Phase 5.3: Builds multi-draw indirect commands (ONE per chunk).
    /// Phase 5.2: Uses optimized vertex layout without normals (28 bytes vs 36 bytes).
    /// Must be called after Phase 3 completes.
    /// Can be called multiple times to update buffers (e.g., when regenerating terrain).
    /// 
    /// PHASE 5.2 STREAMING FIX: Now requires chunk descriptors to handle non-sequential buffer layout.
    /// When chunks are unloaded/reloaded, both vertices AND indices may NOT be sequential in the buffer!
    /// </summary>
    public void SetupBuffers(Phase3BufferManager buffers, uint vertexCount, uint faceCount, IEnumerable<ChunkDescriptor>? chunkDescriptors = null)
    {
        if (disposed)
        {
            Log.Warn("VoxelTerrainRenderer: Cannot setup buffers - renderer is disposed");
            return;
        }

        bufferManager = buffers;
        actualVertexCount = vertexCount;

        const int stride = VoxelHelper.VERTEX_STRIDE_BYTES;

        GL.EnableVertexArrayAttrib(vao, 0);
        GL.VertexArrayAttribFormat(vao, 0, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, 0, 0);

        GL.EnableVertexArrayAttrib(vao, 1);
        GL.VertexArrayAttribFormat(vao, 1, 2, VertexAttribType.Float, false, 12);
        GL.VertexArrayAttribBinding(vao, 1, 0);

        GL.EnableVertexArrayAttrib(vao, 2);
        GL.VertexArrayAttribFormat(vao, 2, 1, VertexAttribType.Float, false, 20);
        GL.VertexArrayAttribBinding(vao, 2, 0);

        GL.EnableVertexArrayAttrib(vao, 3);
        GL.VertexArrayAttribIFormat(vao, 3, 1, VertexAttribIType.UnsignedInt, 24);
        GL.VertexArrayAttribBinding(vao, 3, 0);

        if (buffers.VertexBuffer == 0 || buffers.IndexBuffer == 0)
        {
            Log.Error($"VoxelTerrainRenderer: Invalid buffers! VBO={buffers.VertexBuffer}, IBO={buffers.IndexBuffer}");
            return;
        }

        GL.VertexArrayVertexBuffer(vao, 0, buffers.VertexBuffer, IntPtr.Zero, stride);
        GL.VertexArrayElementBuffer(vao, buffers.IndexBuffer);

        // With GPU-built indirect, the command count equals the last dispatched batch size.
        // We rely on ChunkStreamingManager to bind and dispatch draw using the updated indirect buffer.
        // PHASE 5.3 FIX: Set actualFaceCount to the CommandSlotCapacity so OnDraw draws all potential slots.
        // Empty slots have count=0 and will be skipped by GPU.
        actualFaceCount = buffers.CommandSlotCapacity;

        Log.CheckGlError();

        Log.Info($"VoxelTerrainRenderer: Buffers configured (Phase 5.3: {vertexCount} vertices, {faceCount} faces, {actualFaceCount} commands - GPU built)");
    }

    /// <summary>
    /// Set visibility flags for frustum culling (optional)
    /// Pass null to disable culling (render all chunks)
    /// </summary>
    public void SetVisibilityFlags(int[]? flags, int[]? indices)
    {
        visibilityFlags = flags;
        chunkIndices = indices;

        if (flags != null)
        {
            VisibleDraws = flags.Count(f => f == 1);
        }
        else
        {
            VisibleDraws = chunkIndices?.Length ?? 0;
        }
    }

    /// <summary>
    /// Initialize outline rendering resources (shader, VAO, cube wireframe).
    /// </summary>
    private void InitializeOutlineRendering()
    {
        // Load outline shader
        outlineShader = new Shader("Shaders/block-outline.vert", "Shaders/block-outline.frag");

        // Create cube wireframe vertices (8 corners)
        float[] outlineVertices = [
            // Bottom face
            0.0f, 0.0f, 0.0f,  // 0
            1.0f, 0.0f, 0.0f,  // 1
            1.0f, 0.0f, 1.0f,  // 2
            0.0f, 0.0f, 1.0f,  // 3
            // Top face
            0.0f, 1.0f, 0.0f,  // 4
            1.0f, 1.0f, 0.0f,  // 5
            1.0f, 1.0f, 1.0f,  // 6
            0.0f, 1.0f, 1.0f,  // 7
        ];

        // Create cube wireframe indices (12 edges = 24 indices for GL_LINES)
        uint[] outlineIndices = [
            // Bottom face edges
            0, 1,  1, 2,  2, 3,  3, 0,
            // Top face edges
            4, 5,  5, 6,  6, 7,  7, 4,
            // Vertical edges
            0, 4,  1, 5,  2, 6,  3, 7
        ];

        // Create VAO, VBO, EBO
        GL.CreateVertexArrays(1, out outlineVao);
        GL.CreateBuffers(1, out outlineVbo);
        GL.CreateBuffers(1, out outlineEbo);

        // Upload vertex data
        GL.NamedBufferStorage(outlineVbo, outlineVertices.Length * sizeof(float),
            outlineVertices, BufferStorageFlags.None);

        // Upload index data
        GL.NamedBufferStorage(outlineEbo, outlineIndices.Length * sizeof(uint),
            outlineIndices, BufferStorageFlags.None);

        // Configure VAO
        GL.VertexArrayVertexBuffer(outlineVao, 0, outlineVbo, IntPtr.Zero, 3 * sizeof(float));
        GL.VertexArrayElementBuffer(outlineVao, outlineEbo);

        // Attribute 0: Position (vec3)
        GL.EnableVertexArrayAttrib(outlineVao, 0);
        GL.VertexArrayAttribFormat(outlineVao, 0, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(outlineVao, 0, 0);

        GL.ObjectLabel(ObjectLabelIdentifier.VertexArray, outlineVao, -1, "block_outline_vao");
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, outlineVbo, -1, "block_outline_vbo");
        GL.ObjectLabel(ObjectLabelIdentifier.Buffer, outlineEbo, -1, "block_outline_ebo");

        Log.CheckGlError();
        Log.Info("VoxelTerrainRenderer: Outline rendering initialized");
    }

    /// <summary>
    /// Render outline around the picked block.
    /// </summary>
    private void RenderPickedBlockOutline()
    {
        if (PickedBlock == null || outlineShader == null)
            return;

        // Use outline shader (camera UBO already bound by renderer at binding=0)
        outlineShader.Use();

        // Create transform matrix for the picked block
        var blockPos = PickedBlock.Value.GlobalPosition;
        const float outlineScale = 1.02f; // 2% larger than block
        var transform = Matrix4.CreateScale(outlineScale) *
                       Matrix4.CreateTranslation(blockPos.X, blockPos.Y, blockPos.Z);

        // Set uniforms
        outlineShader.SetMatrix4("uBlockTransform", ref transform);
        var color = outlineColor;
        outlineShader.SetVector3("uOutlineColor", ref color);

        // Render wireframe cube WITH depth test (so it respects solid geometry)
        GL.BindVertexArray(outlineVao);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal); // Render when equal or closer
        GL.DrawElements(PrimitiveType.Lines, 24, DrawElementsType.UnsignedInt, IntPtr.Zero);
        GL.DepthFunc(DepthFunction.Less); // Restore default

        Log.CheckGlError();
    }

    /// <summary>
    /// Override OnDraw to render the voxel terrain using the scene's rendering pipeline.
    /// Phase 5.3: Uses per-chunk index buffers with ONE multi-draw command PER CHUNK!
    /// Camera UBO is already bound by Renderer.RenderNode().
    /// Enables backface culling for proper voxel rendering.
    /// </summary>
    public override void OnDraw(double elapsed)
    {
        if (disposed || bufferManager == null || actualFaceCount == 0)
        {
            return; // Nothing to render yet or already disposed
        }

        // DEBUG: Log that we're rendering
        //if (++frameCounter % 60 == 0) // Log every 60 frames
        //{
        //    Log.Info($"VoxelTerrainRenderer.OnDraw: Rendering {actualFaceCount} chunks via multi-draw indirect");
        //}

        // Enable backface culling for voxel terrain
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        var shader = Material.Shader;

        // DEBUG: Check if shader is valid
        if (shader == null)
        {
            Log.Error("VoxelTerrainRenderer.OnDraw: Material.Shader is NULL!");
            return;
        }

        // Bind the grass-dirt texture atlas
        if (Material.Textures != null && Material.Textures.Length > 0 && Material.Textures[0] != null)
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, Material.Textures[0].Handle);
            shader.SetInt("uBlockTexture", 0);
        }

        // Set material uniforms
        var matSpecular = new Vector3(0.1f, 0.1f, 0.1f);
        shader.SetVector3("uMaterialSpecular", ref matSpecular);
        shader.SetFloat("uMaterialShininess", 8.0f);

        // Chunk transform (identity for now - chunks in world space)
        var identity = Matrix4.Identity;
        shader.SetMatrix4("uChunkTransform", ref identity);

        // Bind VAO (has per-chunk IBO bound)
        GL.BindVertexArray(vao);

        // DEBUG: Verify buffer binding
        if (bufferManager.IndirectDrawBuffer == 0)
        {
            Log.Error("VoxelTerrainRenderer.OnDraw: IndirectDrawBuffer is 0!");
            return;
        }

        // PHASE 5.3: Single multi-draw indirect call with ONE command per chunk!
        // CRITICAL FIX: Use HighWaterMark as draw count, not active chunk count!
        // Slots are allocated sparsely/using free list, so we must draw up to the highest allocated slot.
        // Empty slots (freed) are zeroed out and will be skipped by GPU.
        int drawCount = bufferManager.GetCommandSlotHighWaterMark();

        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, bufferManager.IndirectDrawBuffer);
        GL.MultiDrawElementsIndirect(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            IntPtr.Zero,
            drawCount,             // Draw up to the highest allocated slot
            0                      // Stride (tightly packed)
        );

        // Disable backface culling after rendering (restore default state)
        GL.Disable(EnableCap.CullFace);

        // Render picked block outline (on top of terrain)
        RenderPickedBlockOutline();

        Log.CheckGlError();
    }

    /// <summary>
    /// Cleanup GPU resources. Call this explicitly when removing from scene.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;

        disposed = true;

        if (vao != 0)
        {
            GL.DeleteVertexArray(vao);
            vao = 0;
        }

        // Cleanup outline resources
        if (outlineVao != 0)
        {
            GL.DeleteVertexArray(outlineVao);
            outlineVao = 0;
        }
        if (outlineVbo != 0)
        {
            GL.DeleteBuffer(outlineVbo);
            outlineVbo = 0;
        }
        if (outlineEbo != 0)
        {
            GL.DeleteBuffer(outlineEbo);
            outlineEbo = 0;
        }

        // Don't dispose bufferManager - it's owned by ChunkStreamingManager
        bufferManager = null;
        actualVertexCount = 0;

        Log.Info("VoxelTerrainRenderer: Disposed");

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Get approximate GPU memory allocated for rendering buffers (Phase 5)
    /// </summary>
    public long GetAllocatedBytes()
    {
        if (bufferManager == null) return 0;

        // Vertex buffer: actualVertexCount * stride
        var vertexBytes = actualVertexCount * VoxelHelper.VERTEX_STRIDE_BYTES;

        // Shared index buffer: always 6 indices * 4 bytes
        var indexBytes = 6 * sizeof(uint);

        // Indirect draw commands: actualFaceCount * 5 uints
        var indirectBytes = actualFaceCount * 5 * sizeof(uint);

        return vertexBytes + indexBytes + indirectBytes;
    }

    // Helper methods to satisfy SceneNode constructor requirements
    private static Mesh CreateDummyMesh()
    {
        // Create a minimal dummy mesh - it won't be used for rendering
        var vertices = new Vertex[] { new(0, 0, 0, 0, 1, 0, 0, 0) };
        var indices = new uint[] { 0 };
        return new Mesh(Vertex.VertexDeclaration, vertices, indices);
    }

    private static Material CreateDummyMaterial()
    {
        // Create material with the voxel terrain shader and grass-dirt texture atlas (3x2 layout)
        var shader = new Shader("Shaders/voxel-terrain.vert", "Shaders/voxel-terrain.frag");
        var textureDesc = new TextureDescriptor("Resources/voxel/grass-dirt.png",
            MinFilter: TextureMinFilter.Nearest,
            MagFilter: TextureMagFilter.Nearest,
            TextureWrapS: TextureWrapMode.ClampToEdge,      // CRITICAL: Prevent atlas tile bleeding
            TextureWrapT: TextureWrapMode.ClampToEdge,      // CRITICAL: Prevent atlas tile bleeding
            GenerateMipMap: false);
        return Material.Create(shader, textureDesc);
    }
}
