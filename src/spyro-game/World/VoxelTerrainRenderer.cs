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

    /// <summary>
    /// Whether the camera is currently submerged in water.
    /// Controls underwater fog and lighting effects.
    /// </summary>
    public bool IsCameraUnderwater { get; set; }

    public uint TerrainParamsSSBO { get; set; }
    public int BiomeLutTexture { get; set; }
    public bool ShowBiomes { get; set; }

    // M5: Biome texture handles (bindless)
    // Now stores 3 textures per biome/layer: [BiomeID * 24 + GeologyLayer * 3 + FaceType]
    // FaceType: 0=Top, 1=Bottom, 2=Sides
    private ulong[]? biomeTextureHandles;
    private readonly Dictionary<string, Texture[]> atlasSplitCache = [];

    public void LoadBiomeTextures(TerrainConfig config)
    {
        if (config.Biomes == null || config.Biomes.Count == 0) return;

        // 10 biomes * 8 layers * 3 face types = 240 handles
        var handleCount = 10 * 8 * 3;
        if (biomeTextureHandles == null || biomeTextureHandles.Length != handleCount)
        {
            biomeTextureHandles = new ulong[handleCount];
        }

        // Create a sampler for all terrain textures with REPEAT wrap mode for tiling
        var sampler = Sampler.Create(TextureMinFilter.NearestMipmapNearest, TextureMagFilter.Nearest, TextureWrapMode.Repeat, TextureWrapMode.Repeat);

        for (var i = 0; i < config.Biomes.Count; i++)
        {
            var biome = config.Biomes[i];
            if (biome.Id >= 10) continue; // Max 10 biomes supported in shader for now

            for (var layer = 0; layer < 8; layer++)
            {
                if (layer < biome.TexturePaths.Count)
                {
                    var path = biome.TexturePaths[layer];
                    if (string.IsNullOrEmpty(path))
                    {
                        // Set all 3 face types to 0 for this layer
                        biomeTextureHandles[biome.Id * 24 + layer * 3 + 0] = 0;
                        biomeTextureHandles[biome.Id * 24 + layer * 3 + 1] = 0;
                        biomeTextureHandles[biome.Id * 24 + layer * 3 + 2] = 0;
                        continue;
                    }

                    // Check if we've already split this atlas
                    if (!atlasSplitCache.TryGetValue(path, out var splitTextures))
                    {
                        try 
                        {
                            // Split the 3×1 atlas into 3 separate textures
                            splitTextures = Texture.SplitHorizontalAtlas(path, columns: 3, generateMipMap: true);
                            if (splitTextures != null && splitTextures.Length == 3)
                            {
                                atlasSplitCache[path] = splitTextures;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"Failed to load and split texture atlas '{path}': {ex.Message}");
                        }
                    }

                    if (splitTextures != null && splitTextures.Length == 3)
                    {
                        // Store handles for all 3 face types: Top (0), Bottom (1), Sides (2)
                        biomeTextureHandles[biome.Id * 24 + layer * 3 + 0] = splitTextures[0].GetBindlessHandle(sampler); // Top
                        biomeTextureHandles[biome.Id * 24 + layer * 3 + 1] = splitTextures[1].GetBindlessHandle(sampler); // Bottom
                        biomeTextureHandles[biome.Id * 24 + layer * 3 + 2] = splitTextures[2].GetBindlessHandle(sampler); // Sides
                    }
                }
            }
        }
        
        Log.Info($"VoxelTerrainRenderer: Loaded {biomeTextureHandles.Count(h => h != 0)} biome texture handles (split from atlases)");
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
        // Phase 5.2: Compressed format (2 uints)
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

        // M5: Bind Biome Texture Handles
        if (biomeTextureHandles != null)
        {
            // Try array syntax first, then flat
            var loc = shader.GetUniformLocation("uBiomeTextures[0]");
            if (loc == -1) loc = shader.GetUniformLocation("uBiomeTextures");

            if (loc != -1)
            {
                // Convert ulong[] to uint[] for uvec2 array
                var uints = new uint[biomeTextureHandles.Length * 2];
                for (var i = 0; i < biomeTextureHandles.Length; i++)
                {
                    var h = biomeTextureHandles[i];
                    uints[i * 2] = (uint)(h & 0xFFFFFFFF);
                    uints[i * 2 + 1] = (uint)(h >> 32);
                }

                // Set the uniform array
                // Note: Some drivers/OpenTK versions might have issues with count > 1 if they don't detect array correctly
                GL.Uniform2(loc, biomeTextureHandles.Length, uints);
            }
        }

        // M5: Bind Biome LUT (Unit 7)
        if (BiomeLutTexture != 0)
        {
            GL.ActiveTexture(TextureUnit.Texture7);
            GL.BindTexture(TextureTarget.Texture2D, BiomeLutTexture);
            // Reset active texture to 0 to avoid side effects
            GL.ActiveTexture(TextureUnit.Texture0);
        }

        // M5: Bind Terrain Params (Binding 10)
        if (TerrainParamsSSBO != 0)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, TerrainParamsSSBO);
        }

        // Phase 5.2: Bind ChunkInfo Buffer (Binding 13)
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
        if (bufferManager.IndirectDrawBuffer == 0)
        {
            Log.Error("VoxelTerrainRenderer.OnDraw: IndirectDrawBuffer is 0!");
            return;
        }

        // PHASE 5.3: Single multi-draw indirect call with ONE command per chunk!
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
        // Stride = 2 * sizeof(DrawElementsIndirectCommand) = 2 * 5 * 4 = 40 bytes
        // Offset = 0
        GL.MultiDrawElementsIndirect(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            IntPtr.Zero,
            drawCount,
            40 // Stride
        );

        // 2. Draw Transparent (Command 2 of each pair)
        // Enable blending and disable depth write (optional, but good for water)
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // Disable culling for thin translucent surfaces (water, leaves, glass)
        GL.Disable(EnableCap.CullFace);
        GL.DepthMask(false);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(-0.5f, -1.0f);

        // Stride = 40 bytes
        // Offset = 20 bytes (start of second command)
        GL.MultiDrawElementsIndirect(
            PrimitiveType.Triangles,
            DrawElementsType.UnsignedInt,
            (IntPtr)20, // Offset to second command
            drawCount,
            40 // Stride
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

        // Indirect draw commands: actualFaceCount * 5 * sizeof(uint);
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
        // Create material with the voxel terrain shader
        // Textures are now loaded per-biome via LoadBiomeTextures() from TerrainConfig
        var shader = new Shader("Shaders/voxel-terrain.vert", "Shaders/voxel-terrain.frag");
        
        // Use default placeholder textures (will be overridden by biome textures)
        // NOTE: Actual terrain textures are loaded dynamically via LoadBiomeTextures()
        // based on TerrainConfig biome definitions
        return Material.Create(shader, (TextureDescriptor[]?)null);
    }
}
