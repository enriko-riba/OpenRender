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

    /// <summary>
    /// Custom texture storage to bypass Material system limits (8 slots) and rigid TextureType slots.
    /// Mapped by GeologyLayer for clarity.
    /// </summary>
    public Dictionary<GeologyLayer, Texture> Textures { get; } = new();

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

        // Populate Textures dictionary from Material to support custom binding in OnDraw
        // Mapping Material slots (TextureType) to GeologyLayer
        if (Material.Textures[(int)TextureType.Diffuse] != null) Textures[GeologyLayer.Surface] = Material.Textures[(int)TextureType.Diffuse];
        if (Material.Textures[(int)TextureType.Detail] != null) Textures[GeologyLayer.Water] = Material.Textures[(int)TextureType.Detail];
        if (Material.Textures[(int)TextureType.Additional3] != null) Textures[GeologyLayer.Subsurface] = Material.Textures[(int)TextureType.Additional3]; // Dirt
        if (Material.Textures[(int)TextureType.Specular] != null) Textures[GeologyLayer.DeepSubsurface] = Material.Textures[(int)TextureType.Specular]; // Rock
        if (Material.Textures[(int)TextureType.Bump] != null) Textures[GeologyLayer.ShoreLine] = Material.Textures[(int)TextureType.Bump]; // Sand
        if (Material.Textures[(int)TextureType.Additional2] != null) Textures[GeologyLayer.UnderwaterSubsurface] = Material.Textures[(int)TextureType.Additional2]; // Bedrock

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
        GL.VertexArrayAttribFormat(vao, 0, 3, VertexAttribType.Float, false, 0);
        GL.VertexArrayAttribBinding(vao, 0, 0);

        GL.EnableVertexArrayAttrib(vao, 1);
        GL.VertexArrayAttribFormat(vao, 1, 2, VertexAttribType.Float, false, 12);
        GL.VertexArrayAttribBinding(vao, 1, 0);

        GL.EnableVertexArrayAttrib(vao, 2);
        GL.VertexArrayAttribFormat(vao, 2, 1, VertexAttribType.Float, false, 20);
        GL.VertexArrayAttribBinding(vao, 2, 0);

        GL.EnableVertexArrayAttrib(vao, 3);
        // Phase 5.2: Face Index is stored as float (uintBitsToFloat) in the buffer.
        // We want the raw bits in the shader (uint).
        // VertexAttribType.Float tells GL to read 32-bit float and convert to float/int.
        // If shader input is uint, GL converts float value to uint (e.g. 1.0f -> 1u).
        // BUT we stored bits! uintBitsToFloat(1) is 1.4e-45. GL converts 1.4e-45 to 0u.
        // FIX: Use VertexAttribPointer with type FLOAT but shader input as float, then floatBitsToUint.
        // OR: Use VertexAttribIPointer with type UNSIGNED_INT?
        // If we use VertexAttribIPointer(..., GL_UNSIGNED_INT, ...), GL reads 32 bits as uint.
        // Since the buffer contains the raw bits of the uint (just cast to float for storage),
        // reading them as uint will recover the original uint value!
        // So VertexAttribIFormat with UnsignedInt is correct IF the buffer data is binary compatible.
        // float and uint are both 32-bit. uintBitsToFloat preserves the bit pattern.
        // So the buffer contains the bits of the uint.
        // Reading as UnsignedInt retrieves the bits.
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

        // Bind textures from custom dictionary
        // This bypasses the Material system's 8-texture limit and TextureType slot restrictions.
        if (Textures.Count > 0)
        {
            void Bind(GeologyLayer layer, string uniformName, int unit)
            {
                if (Textures.TryGetValue(layer, out var tex))
                {
                    GL.ActiveTexture(TextureUnit.Texture0 + unit);
                    GL.BindTexture(TextureTarget.Texture2D, tex.Handle);
                    shader.SetInt(uniformName, unit);
                }
            }

            Bind(GeologyLayer.Surface, "uTexSurface", 0);
            Bind(GeologyLayer.Water, "uTexWater", 1);
            Bind(GeologyLayer.Subsurface, "uTexSubSurface", 2);
            Bind(GeologyLayer.DeepSubsurface, "uTexDeep", 3);
            Bind(GeologyLayer.ShoreLine, "uTexShore", 4);
            Bind(GeologyLayer.UnderwaterSubsurface, "uTexUnderwaterSubsurface", 5);
        }

        // Enable blending for water transparency
        // GL.Enable(EnableCap.Blend);
        // GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        // Set material uniforms
        var matSpecular = new Vector3(0.1f, 0.1f, 0.1f);
        shader.SetVector3("uMaterialSpecular", ref matSpecular);
        shader.SetFloat("uMaterialShininess", 8.0f);
        shader.SetInt("uIsUnderwater", IsCameraUnderwater ? 1 : 0);
        shader.SetFloat("uTime", (float)Scene!.SceneManager.Time);
        shader.SetInt("uShowBiomes", ShowBiomes ? 1 : 0);

        // Bind Terrain Params (M4)
        if (TerrainParamsSSBO != 0)
        {
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 10, TerrainParamsSSBO);
        }

        // Bind Biome LUT (M4)
        if (BiomeLutTexture != 0)
        {
            GL.ActiveTexture(TextureUnit.Texture7);
            GL.BindTexture(TextureTarget.Texture2D, BiomeLutTexture);
        }

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
        var drawCount = bufferManager.GetCommandSlotHighWaterMark();

        GL.BindBuffer(BufferTarget.DrawIndirectBuffer, bufferManager.IndirectDrawBuffer);
        
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
        // Disable culling for water to see surface from below
        GL.Disable(EnableCap.CullFace);
        // GL.DepthMask(false); // Optional: Disable depth write for transparent objects if sorting is an issue

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
        // GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
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
        
        // Get paths from VoxelWorld configuration
        var grassPath = VoxelWorld.textures[BlockType.GrassDirt];
        var waterPath = VoxelWorld.textures[BlockType.WaterLevel];
        var dirtPath = VoxelWorld.textures[BlockType.Dirt];
        var rockPath = VoxelWorld.textures[BlockType.Rock];
        var sandPath = VoxelWorld.textures[BlockType.Sand];
        var bedrockPath = VoxelWorld.textures[BlockType.BedRock];
        
        // Slot 0: GrassDirt
        var grassDesc = new TextureDescriptor(grassPath,
            MinFilter: TextureMinFilter.Nearest,
            MagFilter: TextureMagFilter.Nearest,
            TextureType: TextureType.Diffuse, // Slot 0
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge,
            GenerateMipMap: false);

        // Slot 1: Water
        var waterDesc = new TextureDescriptor(waterPath,
            MinFilter: TextureMinFilter.Nearest,
            MagFilter: TextureMagFilter.Nearest,
            TextureType: TextureType.Detail, // Slot 1
            TextureWrapS: TextureWrapMode.Repeat,
            TextureWrapT: TextureWrapMode.Repeat,
            GenerateMipMap: true);

        // Slot 2: Dirt
        var dirtDesc = new TextureDescriptor(dirtPath,
            MinFilter: TextureMinFilter.Nearest,
            MagFilter: TextureMagFilter.Nearest,
            TextureType: TextureType.Additional3, // Slot 6 (Avoid Normal=2 which forces Linear)
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge,
            GenerateMipMap: false);

        // Slot 3: Rock
        var rockDesc = new TextureDescriptor(rockPath,
            MinFilter: TextureMinFilter.Nearest,
            MagFilter: TextureMagFilter.Nearest,
            TextureType: TextureType.Specular, // Slot 3
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge,
            GenerateMipMap: false);

        // Slot 4: Sand
        var sandDesc = new TextureDescriptor(sandPath,
            MinFilter: TextureMinFilter.Nearest,
            MagFilter: TextureMagFilter.Nearest,
            TextureType: TextureType.Bump, // Slot 4
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge,
            GenerateMipMap: false);

        // Slot 5: BedRock
        var bedrockDesc = new TextureDescriptor(bedrockPath,
            MinFilter: TextureMinFilter.Nearest,
            MagFilter: TextureMagFilter.Nearest,
            TextureType: TextureType.Additional2, // Slot 5
            TextureWrapS: TextureWrapMode.ClampToEdge,
            TextureWrapT: TextureWrapMode.ClampToEdge,
            GenerateMipMap: false);

        return Material.Create(shader, [grassDesc, waterDesc, dirtDesc, rockDesc, sandDesc, bedrockDesc]);
    }
}
