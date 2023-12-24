global using AABB = (OpenTK.Mathematics.Vector3i min, OpenTK.Mathematics.Vector3i max);

using OpenRender;
using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Culling;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SpyroGame.Components;
using SpyroGame.Input;
using SpyroGame.World;

namespace SpyroGame;

internal class MainScene(ITextRenderer textRenderer) : Scene
{
    private readonly KeyboardActionMapper kbdActions = new();
    private readonly VoxelWorld world = new();
    private readonly Frustum frustum = new();

    private Sprite crosshair;
    private ChunkRenderer cr;
    private const float MovementSpeed = 30;
    private const float RotationSpeed = 25;
    private float movementPerSecond = MovementSpeed;
    private float rotationPerSecond = RotationSpeed;
    private bool isMouseMoving;

    private const int TotalChunks = VoxelWorld.Size * VoxelWorld.Size * VoxelWorld.Size;
    private const int Padding = 50;

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.DarkSlateBlue;
        var yPosition = Chunk.Size * (VoxelWorld.GroundBaseLevel + 1f)+1;
        camera = new CameraFps(new Vector3(Chunk.Size * VoxelWorld.Size / 2f, yPosition, Chunk.Size * VoxelWorld.Size / 2f), Width / (float)Height, 0.01f, farPlane: 400);
        //camera = new  Camera3D(new Vector3(Chunk.Size * VoxelWorld.Size / 2f, yPosition, Chunk.Size * VoxelWorld.Size / 2f), Width / (float)Height, 0.01f, 400);
        camera.MaxFov = 60;

        var dirLight = new LightUniform()
        {
            Direction = new Vector3(0, -0.90f, -1f),
            Ambient = new Vector3(0.15f, 0.15f, 0.15f),
            Diffuse = new Vector3(1),
            Specular = new Vector3(1),
        };
        AddLight(dirLight);
        kbdActions.AddActions([
            new KeyboardAction("exit", [Keys.Escape], SceneManager.Close),
            new KeyboardAction("full screen toggle", [Keys.F11], FullScreenToggle),
            new KeyboardAction("forward", [Keys.W], ()=> camera!.MoveForward(movementPerSecond), false),
            new KeyboardAction("left", [Keys.A], ()=> camera!.Position -= camera.Right * movementPerSecond, false),
            new KeyboardAction("right", [Keys.D], ()=> camera!.Position += camera.Right * movementPerSecond, false),
            new KeyboardAction("back", [Keys.S], ()=> camera!.MoveForward(-movementPerSecond), false),
            new KeyboardAction("rot CCW", [Keys.Q], ()=> camera!.AddRotation(0, 0, -rotationPerSecond), false),
            new KeyboardAction("rot CW", [Keys.E], ()=> camera!.AddRotation(0, 0, rotationPerSecond), false),
            new KeyboardAction("up", [Keys.LeftShift], ()=> camera!.Position += camera.Up * movementPerSecond, false),
            new KeyboardAction("down", [Keys.LeftControl], ()=> camera!.Position -= camera.Up * movementPerSecond, false),
            new KeyboardAction("wireframe", [Keys.F1], WireframeToggle),
        ]);

        //  create 2D crosshair
        var shader = new Shader("Shaders/sprite.vert", "Shaders/sprite.frag");
        var material = Material.Create(shader,
            [
                new TextureDescriptor("Resources/crosshair.png",
                    TextureType: TextureType.Diffuse,
                    MagFilter: TextureMagFilter.Nearest,
                    MinFilter: TextureMinFilter.Nearest,
                    TextureWrapS: TextureWrapMode.ClampToEdge,
                    TextureWrapT: TextureWrapMode.ClampToEdge,
                    GenerateMipMap: true)
            ]
        );
        var (v, i) = GeometryHelper.Create2dQuad();
        var mesh = new Mesh(Vertex2D.VertexDeclaration, v, i);
        crosshair = new Sprite(mesh, material)
        {
            Tint = Color4.LightPink
        };
        AddNode(crosshair);

        SetupScene();
    }

    private static readonly Vector3 textColor = Color4.Black.ToVector3();

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}";
        textRenderer.Render(fpsText, 20, 5, 4, textColor);
        var text = $"World size: {VoxelWorld.Size:N0}, total chunks: {TotalChunks:N0}";
        textRenderer.Render(text, 20, 5, 20, textColor);
        text = $"Chunks visible: {cr.VisibleChunks:N0} culled: {TotalChunks - cr.VisibleChunks:N0}";
        textRenderer.Render(text, 20, 5, 36, textColor);
        text = $"Blocks rendered: {cr.RenderedBlocks:N0}";
        textRenderer.Render(text, 20, 5, 52, textColor);

        text = $"position: {camera?.Position}";
        textRenderer.Render(text, 20, 5, 68, Vector3.UnitZ);

        //  show chunk index and position
        var cameraChunkPosition = Chunk.Size * ((Vector3i)camera!.Position / Chunk.Size);
        if (world.GetChunkByWorldPosition(cameraChunkPosition, out var chunk))
        {
            text = $"in chunk: {chunk.Index} at {chunk.Position}";
            textRenderer.Render(text, 20, 5, 84, Vector3.UnitZ);
        }
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);

        frustum.Update(camera!);

        movementPerSecond = (float)elapsedSeconds * MovementSpeed;
        rotationPerSecond = (float)(elapsedSeconds * RotationSpeed);
        kbdActions.Update(SceneManager.KeyboardState);

        var mouseState = SceneManager.MouseState;
        if (isMouseMoving && mouseState.IsButtonDown(MouseButton.Left))
        {
            if (SceneManager.MouseState.Delta.LengthSquared > 0)
            {
                camera!.AddRotation(mouseState.Delta.X * rotationPerSecond, mouseState.Delta.Y * rotationPerSecond, 0);
            }
        }
        else
        {
            isMouseMoving = false;
        }

        if (!isMouseMoving && mouseState.IsButtonPressed(MouseButton.Left))
        {
            isMouseMoving = true;
        }
    }

    public override void OnMouseWheel(MouseWheelEventArgs e)
    {
        const float Sensitivity = 3.5f;
        camera!.Fov -= e.OffsetY * Sensitivity;
    }

    public override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        textRenderer.Projection = TextRenderer.CreateTextRenderingProjection(Width, Height);
        var clientSize = new Vector2(Width, Height);
        crosshair.SetScale(1);
        crosshair.SetPosition((clientSize - crosshair.Size)/2);
        crosshair.Pivot = new(0.0f, 1.0f);
    }

    private void FullScreenToggle() => SceneManager.WindowState = SceneManager.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;

    private void WireframeToggle() => ShowBoundingSphere = !ShowBoundingSphere;

    private void SetupScene()
    {
        var paths = new string[] {
            "Resources/skybox/right.png",
            "Resources/skybox/left.png",
            "Resources/skybox/top.png",
            "Resources/skybox/bottom.png",
            "Resources/skybox/back.png",
            "Resources/skybox/front.png",
        };
        var skyBox = SkyBox.Create(paths);
        AddNode(skyBox);

        var (vertices, indices) = CreateVoxelCube();
        var cubeMaterial = Material.Create(defaultShader,
            [new("Resources/voxel/box-unwrap.png", TextureType: TextureType.Diffuse)],
            //[new("Resources/Corey.png", TextureType: TextureType.Diffuse)],
            0.10f);
        cubeMaterial.EmissiveColor = new(0.05f, 0.07f, 0.005f);
        var cube = new SceneNode(new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices), cubeMaterial);
        cube.SetPosition(new(0, 0, -15));
        AddNode(cube);
        cube.GetTransform(out var transform);


        //  create voxel world

        //  prepare all textures and all materials
        var textureHandles = GetTextureHandles(world);
        var materials = GetMaterials(world);

        //  create dummy material. Only the shader is important, the voxel materials and textures are passed in SSBOs
        var shader = new Shader("Shaders/instancedChunk.vert", "Shaders/instancedChunk.frag");
        var dummyMaterial = Material.Default;    //  TODO: implement CreateNew() or CreateDefault() in Material
        dummyMaterial.Shader = shader;
        var mesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
        cr = new ChunkRenderer(mesh, dummyMaterial, textureHandles, materials);
        AddNode(cr);

        //  find chunks nearest to camera to display them first
        //var chunksData = world.GetImmediate(camera!.Position, 27);
        //foreach (var (chunk, aabb, bs) in chunksData)
        //{
        //    cr.AddChunkData(chunk, bs.ToArray());
        //}

        //camera!.Update();
        //frustum.Update(camera);
        //cr.OnUpdate(this, 1);


        var sortedChunks = world.chunks.Values
                .OrderBy(x => Vector3.DistanceSquared(x.Position + VoxelWorld.ChunkHalfSize, camera!.Position))    
                .ToArray();

        //  the only purpose of this list is to enable parallel processing and caching of BlockDataBufferElement
        //  so that the createChunkRenderData can be called after parallel execution is completed
        var blockDataList = new List<(Chunk, BlockState[])>(world.chunks.Count);
        Log.Info("calculating visible blocks...");
        world.stopwatch.Restart();       
        Parallel.ForEach(sortedChunks, (chunk) =>
        {
            if (!cr.IsChunkAdded(chunk.Position))
            {
                //Log.Debug($"processing chunk {chunk.Index} on thread {Environment.CurrentManagedThreadId}");
                var blockData = chunk.GetVisibleBlocks().ToArray();
                blockDataList.Add((chunk, blockData));
            }
        });

        Log.Highlight($"visible blocks calculated in {world.stopwatch.Elapsed}!");
        world.stopwatch.Restart();

        foreach (var (chunk, blockData) in blockDataList)
        {
            cr.AddChunkData(chunk, blockData);
        }
        camera!.Update();
        frustum.Update(camera);
        cr.OnUpdate(this, 1);
        Log.Highlight($"chunk render data created in {world.stopwatch.ElapsedMilliseconds} ms!");
    }

    private static (Vertex[], uint[]) CreateVoxelCube()
    {
        const float HALF = 0.5f;

        Vertex[] vertices =
        [
            // Position                 Normal      Texture
            //  FRONT SIDE (z = +)
            new (-HALF, -HALF,  HALF,   0,  0,  1,   0.666f, 0.5f),     // lower left - 0
            new (-HALF,  HALF,  HALF,   0,  0,  1,   0.666f, 1f),       // upper left - 1
            new ( HALF, -HALF,  HALF,   0,  0,  1,   1f, 0.5f),         // lower right - 2
            new ( HALF,  HALF,  HALF,   0,  0,  1,   1f, 1f),           // upper right - 3
                                           
            //  BACK SIDE (z = -)
            new (-HALF, -HALF, -HALF,   0,  0, -1,   1, 0),             // lower left
            new (-HALF,  HALF, -HALF,   0,  0, -1,   1, 0.5f),          // upper left
            new ( HALF, -HALF, -HALF,   0,  0, -1,   0.666f, 0),        // lower right
            new ( HALF,  HALF, -HALF,   0,  0, -1,   0.666f, 0.5f),     // upper right
                                                               
            //  LEFT SIDE (X = -)
            new (-HALF, -HALF, -HALF,  -1,  0,  0,   0, 0),             // lower left  - 8
            new (-HALF,  HALF, -HALF,  -1,  0,  0,   0, 0.5f),          // upper left - 9
            new (-HALF, -HALF,  HALF,  -1,  0,  0,   0.333f, 0),        // lower right - 10
            new (-HALF,  HALF,  HALF,  -1,  0,  0,   0.333f, 0.5f),     // upper right - 11

            //  RIGHT SIDE (X = +)
            new (HALF, -HALF,  HALF,   1,  0,  0,   0.333f, 0),         // lower left  - 12
            new (HALF,  HALF,  HALF,   1,  0,  0,   0.333f, 0.5f),      // upper left - 13
            new (HALF, -HALF, -HALF,   1,  0,  0,   0.666f, 0),         // lower right - 14
            new (HALF,  HALF, -HALF,   1,  0,  0,   0.666f, 0.5f),      // upper right - 15            
            
            //  TOP SIDE (Y = +)
            new (-HALF,  HALF,  HALF,   0,  1,  0,   0, 0.5f),          // lower left - 16
            new (-HALF,  HALF, -HALF,   0,  1,  0,   0, 1),             // upper left - 17
            new ( HALF,  HALF,  HALF,   0,  1,  0,   0.333f, 0.5f),     // lower right - 18
            new ( HALF,  HALF, -HALF,   0,  1,  0,   0.333f, 1),        // upper right - 19

            //  BOTTOM SIDE (Y = -)
            new (-HALF, -HALF, -HALF,   0, -1,  0,   0.333f, 0.5f),     // lower left - 20
            new (-HALF, -HALF,  HALF,   0, -1,  0,   0.333f, 1),        // upper left - 21
            new ( HALF, -HALF, -HALF,   0, -1,  0,   0.666f, 0.5f),     // lower right - 22
            new ( HALF, -HALF,  HALF,   0, -1,  0,   0.666f, 1),        // upper right - 23             
        ];
        uint[] indices =
        [
            // front quad
            2, 1, 0,
            2, 3, 1,

            // back quad
            4, 7, 6,
            4, 5, 7,

            // left quad
            10, 9, 8,
            10, 11, 9,

            // right quad
            14, 13, 12,
            14, 15, 13,
            
            // up quad            
            18, 17, 16,
            18, 19, 17,

            // down quad                                
            22, 21, 20,
            22, 23, 21
        ];

        return (vertices, indices);
    }

    /// <summary>
    /// Prepares all textures for the voxel world.
    /// </summary>
    /// <param name="world"></param>
    /// <returns></returns>
    private static ulong[] GetTextureHandles(VoxelWorld world)
    {
        var textureNames = Enumerable.Range(0, world.textures.Keys.Cast<int>().Max() + 1)
            .Select(index => world.textures.ContainsKey((BlockType)index) ? world.textures[(BlockType)index] : null)
            .ToArray();
        var sampler = Sampler.Create(TextureMinFilter.NearestMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.ClampToBorder, TextureWrapMode.ClampToBorder);
        var handles = textureNames.Where(x => !string.IsNullOrEmpty(x))
                                .Select(x => Texture.FromFile([x!])
                                .GetBindlessHandle(sampler))
                                .ToArray();
        return handles;
    }

    /// <summary>
    /// Prepares all materials for the voxel world.
    /// </summary>
    /// <param name="world"></param>
    /// <returns></returns>
    private static VoxelMaterial[] GetMaterials(VoxelWorld world)
    {
        var materials = Enumerable.Range(0, world.materials.Keys.Max() + 1)
            .Select(index => world.materials.TryGetValue(index, out var value) ? value : default)
            .ToArray();
        return materials;
    }
}
