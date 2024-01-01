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
    private DayNightCycle dayNightCycle = default!;

    private Sprite crosshair = default!;
    private ChunkRenderer cr = default!;
    private const float MovementSpeed = 4;
    private const float RotationSpeed = 20;
    private float movementPerSecond = MovementSpeed;
    private float rotationPerSecond = RotationSpeed;
    private bool isMouseMoving;

    private const int Padding = 50;

    private static readonly Vector3 textColor = new(0.3f);
    private Chunk? cameraCurrentChunk;
    private Vector3i cameraCurrentChunkLocalPosition;
    private LightUniform dirLight;

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.DarkSlateBlue;

        camera = new CameraFps(new Vector3(VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f, 0, VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f), Width / (float)Height, 0.1f, VoxelHelper.ChunkSideSize * 8);
        //camera = new  Camera3D(new Vector3(VoxelHelper.ChunkSideSize * VoxelHelper.WorldSize / 2f, yPosition, VoxelHelper.ChunkSideSize * VoxelHelper.WorldSize / 2f), Width / (float)Height, 0.01f, 320);
        camera.MaxFov = 60;

        dirLight = new LightUniform()
        {
            Direction = new Vector3(0, -1, 0),
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
            new KeyboardAction("up", [Keys.LeftShift], ()=> camera!.Position += Vector3.UnitY * movementPerSecond, false),
            new KeyboardAction("down", [Keys.LeftControl], ()=> camera!.Position -= Vector3.UnitY * movementPerSecond, false),
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

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}";
        textRenderer.Render(fpsText, 20, 5, 4, textColor);
        var text = $"World size: {VoxelHelper.WorldChunksXZ:N0}, total chunks: {VoxelHelper.TotalChunks:N0}";
        textRenderer.Render(text, 20, 5, 20, textColor);
        text = $"Chunks in frustum: {cr.ChunksInFrustum:N0} culled: {VoxelHelper.TotalChunks - cr.ChunksInFrustum:N0} hidden: {cr.HiddenChunks:N0}";
        textRenderer.Render(text, 20, 5, 36, textColor);
        text = $"Blocks rendered: {cr.RenderedBlocks:N0}";
        textRenderer.Render(text, 20, 5, 52, textColor);

        text = $"position: {camera?.Position}";
        textRenderer.Render(text, 20, 5, 68, Vector3.UnitZ);

        //  show chunk index and position
        if (cameraCurrentChunk is not null)
        {
            text = $"in chunk: {cameraCurrentChunk.Value} at {cameraCurrentChunk.Value.GetBlockAtLocalXZ(cameraCurrentChunkLocalPosition)}";
            textRenderer.Render(text, 20, 5, 84, Vector3.UnitZ);
        }

        text = $"time: {dayNightCycle.TimeOfDay}";
        textRenderer.Render(text, 20, 5, 104, new(0.5f));
        text = $"sun direction: {dayNightCycle.DirLight.Direction}, ambient: {dayNightCycle.DirLight.Ambient}";
        textRenderer.Render(text, 20, 5, 120, new(0.5f));

        text = $"camera direction: {camera!.Front}";
        textRenderer.Render(text, 20, 5, 140, new(0.5f));
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);

        frustum.Update(camera!);

        var pos = camera!.Position;
        if (world.GetChunkByGlobalPosition(pos, out var chunk))
        {
            cameraCurrentChunk = chunk;
            cameraCurrentChunkLocalPosition = VoxelHelper.GetBlockCoordinatesGlobal(pos) - chunk!.Value.Position;
            var height = chunk.Value.GetHeightNormalized(cameraCurrentChunkLocalPosition.X, cameraCurrentChunkLocalPosition.Z);
            pos.Y = MathF.Floor(height) + 3f;
            camera.Position = pos;
        }
        else
        {
            cameraCurrentChunk = null;
        }

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

        dayNightCycle.Update();        
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
        crosshair.SetPosition((clientSize - crosshair.Size) / 2);
        crosshair.Pivot = new(0.0f, 1.0f);
    }

    private void FullScreenToggle() => SceneManager.WindowState = SceneManager.WindowState == WindowState.Fullscreen ? WindowState.Normal : WindowState.Fullscreen;

    private void WireframeToggle() => ShowBoundingSphere = !ShowBoundingSphere;

    private void SetupScene()
    {
        dayNightCycle = new(this);
        

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
        cube.SetPosition(new(0, 0, 0));
        AddNode(cube);

        Log.Highlight($"initializing voxel world...");
        var seed = 1338;
        world.Initialize(seed);

        //  create voxel world
        Log.Highlight($"preparing voxel textures and materials...");
        world.stopwatch.Restart();

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
        Log.Highlight($"textures and materials prepared in {world.stopwatch.ElapsedMilliseconds:D4} ms");
        world.stopwatch.Restart();

        //  find chunks nearest to camera to display them first
        //var chunksData = world.GetImmediate(camera!.Position, 12);
        //foreach (var (chunk, aabb, bs) in chunksData)
        //{
        //    cr.AddChunkData(chunk, bs.ToArray());
        //}
        //camera!.Update();
        //frustum.Update(camera);
        //cr.OnUpdate(this, 1);
        //Log.Highlight($"initial chunks calculated in {world.stopwatch.ElapsedMilliseconds:D4} ms");
        //world.stopwatch.Restart();

        var sortedChunks = world.chunks.Values
            //.Except(chunksData.Select(x => x.Item1))
            .OrderBy(x => Vector3.DistanceSquared(x.Position + VoxelWorld.ChunkHalfSize, camera!.Position))
            .ToArray();

        Log.Info("calculating visible blocks...");
        world.stopwatch.Restart();

        var task = Task.Run(() =>
        {
            var options = new ParallelOptions() { MaxDegreeOfParallelism = VoxelHelper.WorldChunksXZ * 2 };
            Parallel.ForEach(sortedChunks, options, (chunk) =>
            {
                try
                {
                    if (!cr.IsChunkAdded(chunk.Position))
                    {
                        var blockData = chunk.GetVisibleBlocks().ToArray();
                        cr.blockDataPriorityWorkQueue.Enqueue((chunk, blockData));
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"{ex.Message} chunk {chunk.Index}");
                }
            });
            Log.Highlight($"visible blocks calculated in {world.stopwatch.Elapsed}!");
            world.stopwatch.Restart();
        });


        dayNightCycle.Update();
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
        var sampler = Sampler.Create(TextureMinFilter.NearestMipmapLinear, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
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
