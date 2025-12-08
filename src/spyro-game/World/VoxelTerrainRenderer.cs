using OpenRender;
using OpenRender.Core;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace SpyroGame.World;

/// <summary>
/// Manages rendering of CPU-generated voxel terrain.
/// Consumes output from mesh buffers (compacted vertices and indices).
/// Integrates with the scene graph as a SceneNode.
/// </summary>
public class VoxelTerrainRenderer : SceneNode, IDisposable
{
    private uint vao;
    private TerrainMeshBufferManager? bufferManager;
    private uint actualVertexCount;
    private uint commandCapacity;           // Track command capacity instead
    private bool disposed;

    // Visibility tracking (for frustum culling)
    private int[]? visibilityFlags;
    private int[]? chunkIndices;

    // Block outline rendering
    private Shader? outlineShader;
    private uint outlineVao;
    private uint outlineVbo;
    private uint outlineEbo;
    private readonly Vector3 outlineColor = new(1.0f, 1.0f, 0.0f); // Yellow

    // Debug wireframe rendering
    private bool debugWireframe = false;
    
    /// <summary>
    /// Gets or sets whether to render terrain in debug wireframe mode.
    /// When enabled, shows triangle edges and chunk boundaries.
    /// </summary>
    public bool DebugWireframe 
    { 
        get => debugWireframe;
        set => debugWireframe = value;
    }
    
    /// <summary>
    /// Number of visible chunks (after frustum culling)
    /// </summary>
    public int VisibleDraws { get; private set; }

    /// <summary>
    /// Total number of draw commands (one per chunk)
    /// </summary>
    public uint RenderedBlocks => commandCapacity;  // Now stores command count, not face count

    /// <summary>
    /// Total draw call count (2 multi-draw indirect calls: Opaque + Transparent)
    /// </summary>
    public int DrawCallCount => commandCapacity > 0 ? 2 : 0;

    /// <summary>
    /// The currently picked/highlighted block (used for outlining/breaking).
    /// Set externally by game logic (e.g., raycasting).
    /// </summary>
    public BlockState? PickedBlock { get; set; }

    /// <summary>
    /// Whether the camera is currently submerged in water.
    /// Controls underwater fog and lighting effects.
    /// </summary>
    public bool IsCameraUnderwater { get; set; }

    public bool ShowBiomes { get; set; }

    private readonly Dictionary<string, Texture[]> atlasSplitCache = [];

    // Block texture system (per MINECRAFT_TERRAIN_ARCHITECTURE.md)
    private BlockTextureManager? blockTextureManager;

    /// <summary>
    /// Initialize the block-based texture system.
    /// This replaces the old biome-based texture loading.
    /// </summary>
    public void InitializeBlockTextures()
    {
        blockTextureManager?.Dispose();
        blockTextureManager = new BlockTextureManager();
        blockTextureManager.Initialize();
    }

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
    /// Set up VAO to use compacted mesh buffers.
    /// Builds multi-draw indirect commands (ONE per chunk).
    /// Uses optimized vertex layout without normals (28 bytes vs 36 bytes).
    /// Must be called after mesh generation completes.
    /// Can be called multiple times to update buffers (e.g., when regenerating terrain).
    /// 
    /// STREAMING FIX: Now requires chunk descriptors to handle non-sequential buffer layout.
    /// When chunks are unloaded/reloaded, both vertices AND indices may NOT be sequential in the buffer!
    /// </summary>
    public void SetupBuffers(TerrainMeshBufferManager buffers, uint vertexCount, uint faceCount)
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
        // Compressed format (2 uints)
        // Use VertexAttribIFormat for integer attributes
        GL.VertexArrayAttribIFormat(vao, 0, 2, VertexAttribIType.UnsignedInt, 0);
        GL.VertexArrayAttribBinding(vao, 0, 0);

        // Disable unused attributes
        GL.DisableVertexArrayAttrib(vao, 1);
        GL.DisableVertexArrayAttrib(vao, 2);
        GL.DisableVertexArrayAttrib(vao, 3);

        if (buffers.VertexBuffer == 0 || buffers.IndexBuffer == 0)
        {
            Log.Error($"VoxelTerrainRenderer: Invalid buffers! VBO={buffers.VertexBuffer}, IBO={buffers.IndexBuffer}");
            return;
        }

        GL.VertexArrayVertexBuffer(vao, 0, buffers.VertexBuffer, IntPtr.Zero, stride);
        GL.VertexArrayElementBuffer(vao, buffers.IndexBuffer);

        // With GPU-built indirect, the command count equals the last dispatched batch size.
        // We rely on ChunkStreamingManager to bind and dispatch draw using the updated indirect buffer.
        // FIX: Set commandCapacity to the CommandSlotCapacity so OnDraw draws all potential slots.
        // Empty slots have count=0 and will be skipped by GPU.
        commandCapacity = buffers.CommandSlotCapacity;

        Log.CheckGlError();

        Log.Debug($"VoxelTerrainRenderer: Buffers configured : {vertexCount} vertices, {faceCount} faces, {commandCapacity} commands");
    }

    /// <summary>
    /// Set visibility flags for frustum culling (optional)
    /// Pass null to disable culling (render all chunks)
    /// </summary>
    public void SetVisibilityFlags(int[]? flags, int[]? indices)
    {
        visibilityFlags = flags;
        chunkIndices = indices;

        VisibleDraws = flags != null ? flags.Count(f => f == 1) : chunkIndices?.Length ?? 0;
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
    /// Uses per-chunk index buffers with ONE multi-draw command PER CHUNK!
    /// Camera UBO is already bound by Renderer.RenderNode().
    /// Enables backface culling for proper voxel rendering.
    /// </summary>
    public override void OnDraw(double elapsed)
    {
        if (disposed || bufferManager == null || commandCapacity == 0)
        {
            return; // Nothing to render yet or already disposed
        }

        // Enable backface culling for voxel terrain
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        // Enable Depth Test for opaque terrain rendering
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Less);

        var shader = Material.Shader;

        // DEBUG: Check if shader is valid
        if (shader == null)
        {
            Log.Error("VoxelTerrainRenderer.OnDraw: Material.Shader is NULL!");
            return;
        }

        // Bind block texture array (Unit 0)
        if (blockTextureManager != null)
        {
            blockTextureManager.Bind(0);
            shader.SetInt("uBlockTextures", 0);
        }

        // Bind ChunkInfo Buffer (Binding 13)
        if (bufferManager != null && bufferManager.ChunkInfoBuffer != 0)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 13, bufferManager.ChunkInfoBuffer);
        }

        // Chunk transform (identity for now - chunks in world space)
        var identity = Matrix4.Identity;
        shader.SetMatrix4("uChunkTransform", ref identity);

        // Bind VAO (has per-chunk IBO bound)
        GL.BindVertexArray(vao);

        // Set debug uniforms
        shader.SetInt("uShowBiomes", ShowBiomes ? 1 : 0);
        shader.SetInt("uIsUnderwater", IsCameraUnderwater ? 1 : 0);
        shader.SetUInt("uWorldChunksXZ", (uint)VoxelHelper.WorldChunksXZ);

        // DEBUG: Verify buffer binding
        if (bufferManager!.IndirectDrawBuffer == 0)
        {
            Log.Error("VoxelTerrainRenderer.OnDraw: IndirectDrawBuffer is 0!");
            return;
        }

        // Single multi-draw indirect call with ONE command per chunk!
        // CRITICAL FIX: Use HighWaterMark as draw count, not active chunk count!
        // Slots are allocated sparsely/using free list, so we must draw up to the highest allocated slot.
        // Empty slots (freed) are zeroed out and will be skipped by GPU.
        var drawCount = bufferManager.GetCommandSlotHighWaterMark();

        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, bufferManager.IndirectDrawBuffer);
        
        // If wireframe mode is enabled, render as wireframe
        if (debugWireframe)
        {
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
        }
        
        // 1. Draw Opaque (Command 1 of each pair)
        // Stride = 3 * sizeof(DrawElementsIndirectCommand) = 3 * 5 * 4 = 60 bytes
        // Offset = 0
        GL.Disable(EnableCap.Blend); // Ensure blending is off for opaque pass
        shader.SetInt("uIsCubeletPass", 0); // Ensure default state
        GL.MultiDrawElementsIndirect(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            IntPtr.Zero,
            drawCount,
            60 // Stride
        );

        // 2. Draw Water (Command 2)
        // Enable blending and disable depth write (optional, but good for water)
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // Disable culling for thin translucent surfaces (water, leaves, glass)
        GL.Disable(EnableCap.CullFace);
        GL.DepthMask(false);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-0.5f, -1.0f);

        // Stride = 60 bytes
        // Offset = 20 bytes (start of second command)
        GL.MultiDrawElementsIndirect(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            (IntPtr)20, // Offset to second command
            drawCount,
            60 // Stride
        );

        // 3. Draw Translucent (Command 3)
        // Keep same state as Water (Blend, No Depth Write, No Cull)
        // Stride = 60 bytes
        // Offset = 40 bytes (start of third command)
        GL.MultiDrawElementsIndirect(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            (IntPtr)40, // Offset to third command
            drawCount,
            60 // Stride
        );

        // Restore state
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.DepthMask(true);
        GL.DepthFunc(DepthFunction.Less);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.CullFace);
        
        // Restore fill mode if wireframe was enabled
        if (debugWireframe)
        {
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        }

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

        // Cleanup block textures
        blockTextureManager?.Dispose();
        blockTextureManager = null;

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
    /// Get approximate GPU memory allocated for rendering buffers
    /// </summary>
    public long GetAllocatedBytes()
    {
        if (bufferManager == null) return 0;

        // Vertex buffer: actualVertexCount * stride
        var vertexBytes = actualVertexCount * VoxelHelper.VERTEX_STRIDE_BYTES;

        // Shared index buffer: always 6 indices * 4 bytes
        var indexBytes = 6 * sizeof(uint);

        // Indirect draw commands: commandCapacity * 5 * sizeof(uint);
        var indirectBytes = commandCapacity * 5 * sizeof(uint);

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
        // Create material with the voxel terrain shader
        // Textures are now loaded per-biome via LoadBiomeTextures() from TerrainConfig
        var shader = new Shader("Shaders/voxel-terrain.vert", "Shaders/voxel-terrain.frag");
        
        // Use default placeholder textures (will be overridden by biome textures)
        // NOTE: Actual terrain textures are loaded dynamically via LoadBiomeTextures()
        // based on TerrainConfig biome definitions
        return Material.Create(shader, (TextureDescriptor[]?)null);
    }
}
