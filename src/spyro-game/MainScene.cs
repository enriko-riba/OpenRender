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
    private DayNightCycle dayNightCycle = default!;

    private Sprite crosshair = default!;
    private ChunkRenderer cr = default!;
    private const float MovementSpeed = 4;
    private const float RotationSpeed = 20;
    private float movementPerSecond = MovementSpeed;
    private float rotationPerSecond = RotationSpeed;
    private bool isMouseMoving;

    private const int Padding = 50;

    private static readonly Vector3 textColor = new(0.752f, 0.750f, 0);
    private static readonly Vector3 debugColorBluish = new(0.2f, 0.2f, 1f);
    private Chunk? cameraCurrentChunk;
    private Vector3i cameraCurrentChunkLocalPosition;
    private LightUniform dirLight;
    private VoxelWorld world = default!;

    //  key is material id, value is the material
    private readonly Dictionary<int, VoxelMaterial> materials = new() {
        { (int)BlockType.None, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.7f, 0.8f, 0.9f),
                Shininess = 0.999f
            }
        },
        { (int)BlockType.Rock, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.5f, 0.5f, 0.5f),
                Shininess = 0.15f
            }
        },
        { (int)BlockType.Dirt, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.0f, 0.0f, 0.0f),
                Shininess = 0.0f
            }
        },
        { (int)BlockType.GrassDirt, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.3f, 0.5f, 0.3f),
                Shininess = 0.4f
            }
        },
        { (int)BlockType.Grass, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.3f, 0.5f, 0.3f),
                Shininess = 0.5f
            }
        },
        { (int)BlockType.Sand, new VoxelMaterial() {
                Diffuse = new Vector3(1),
                Emissive = new Vector3(0),
                Specular = new Vector3(1),
                Shininess = 0.3f
            }
        },
        { (int)BlockType.Snow, new VoxelMaterial() {
                Diffuse = new Vector3(1, 1, 1),
                Emissive = new Vector3(0.01f, 0.01f, 0.01f),
                Specular = new Vector3(1),
                Shininess = 0.65f
            }
        },
        { (int)BlockType.WaterLevel, new VoxelMaterial() {
                Diffuse = new Vector3(1, 1, 1),
                Emissive = new Vector3(0),
                Specular = new Vector3(0.7f, 0.7f, 0.8f),
                Shininess = 0.3f
            }
        }
    };

    //  key is texture name, value texture handle index 
    private readonly Dictionary<BlockType, string> textures = new() {
        { BlockType.None, "Resources/voxel/box-unwrap.png" },
        //{ BlockType.UnderWater, "Resources/voxel/under-water.png" },
        { BlockType.WaterLevel, "Resources/voxel/water.png" },
        { BlockType.Rock, "Resources/voxel/rock.png" },
        { BlockType.Sand, "Resources/voxel/sand.png" },
        { BlockType.Dirt, "Resources/voxel/dirt.png" },
        { BlockType.GrassDirt, "Resources/voxel/grass-dirt.png" },
        { BlockType.Grass, "Resources/voxel/grass.png" },
        { BlockType.Snow, "Resources/voxel/snow.png" },
    };

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.DarkSlateBlue;

        var startPosition = new Vector3(VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f, 0, VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f);
        //var startPosition = new Vector3(9050, 100, 3015);
        camera = new CameraFps(startPosition, Width / (float)Height, 0.1f, VoxelHelper.FarPlane)
        {
            MaxFov = 60
        };
        dirLight = new LightUniform()
        {
            Direction = new Vector3(0, -1, 0),
            Ambient = new Vector3(0.35f, 0.35f, 0.35f),
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

        var lineY = 4;
        void writeLine(string text, in Vector3 color)
        {
            textRenderer.Render(text.PadRight(70, ' '), 20, 5, lineY, color);
            lineY += 20;
        }
        writeLine("", textColor);

        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}";
        writeLine(fpsText, textColor);

        var text = $"World: size {VoxelHelper.WorldChunksXZ:N0}, chunks {VoxelHelper.TotalChunks:N0}, chunk size: {VoxelHelper.ChunkSideSize}, max chunk dist.: {VoxelHelper.MaxDistanceInChunks}";
        writeLine(text, textColor);
       
        var surroundingChunks = world.SurroundingChunkIndices.Count;
        text = $"Chunks: streamed {surroundingChunks}, in frustum {cr.ChunksInFrustum:N0}/{surroundingChunks - cr.ChunksInFrustum:N0}";
        writeLine(text, textColor);

        text = $"Blocks rendered: {cr.RenderedBlocks:N0}";
        writeLine(text, textColor);

        text = $"position: {camera?.Position}";
        writeLine(text, debugColorBluish);

        //  show chunk index and position
        if (cameraCurrentChunk is not null)
        {
            text = $"in chunk: {cameraCurrentChunk.Value} at {cameraCurrentChunk.Value.GetTopBlockAtLocalXZ(cameraCurrentChunkLocalPosition)}";
            writeLine(text, debugColorBluish);
        }

        text = $"time: {dayNightCycle.TimeOfDay}";
        writeLine(text, Vector3.UnitY);
        writeLine("", textColor);
        //text = $"sun direction: {dayNightCycle.DirLight.Direction}, ambient: {dayNightCycle.DirLight.Ambient}";
        //textRenderer.Render(text, 20, 5, 120, new(0.5f));

        //text = $"camera direction: {camera!.Front}";
        //textRenderer.Render(text, 20, 5, 140, new(0.5f));
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);

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
        world = new(this, 1338);
        

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


        //  create voxel world
        Log.Highlight($"preparing voxel textures and materials...");
        world.stopwatch.Restart();

        //  prepare all textures and all materials
        var textureHandles = GetTextureHandles();
        var materials = GetMaterials();

        //  create dummy material. Only the shader is important, the voxel materials and textures are passed in SSBOs
        var shader = new Shader("Shaders/instancedChunk.vert", "Shaders/instancedChunk.frag");
        var dummyMaterial = Material.Default;    //  TODO: implement CreateNew() or CreateDefault() in Material
        dummyMaterial.Shader = shader;
        var mesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
        cr = new ChunkRenderer(world, mesh, dummyMaterial, textureHandles, materials);
        AddNode(cr);
        Log.Highlight($"textures and materials prepared in {world.stopwatch.ElapsedMilliseconds:D4} ms");
        world.stopwatch.Restart();

        Log.Highlight($"initializing voxel world...");
        world.AddStartingChunks(camera.Position, cr.initializedChunksQueue.Enqueue);

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
    private ulong[] GetTextureHandles()
    {
        var textureNames = Enumerable.Range(0, textures.Keys.Cast<int>().Max() + 1)
            .Select(index => textures.ContainsKey((BlockType)index) ? textures[(BlockType)index] : null)
            .ToArray();
        var sampler = Sampler.Create(TextureMinFilter.NearestMipmapNearest, TextureMagFilter.Linear, TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge);
        var handles = textureNames
                        .Select(x => string.IsNullOrEmpty(x) ? null : Texture.FromFile([x!]))
                        .Select(x => x?.GetBindlessHandle(sampler) ?? 0)
                        .ToArray();

        return handles;
    }

    /// <summary>
    /// Prepares all materials for the voxel world.
    /// </summary>
    /// <param name="world"></param>
    /// <returns></returns>
    private VoxelMaterial[] GetMaterials()
    {
        return Enumerable.Range(0, materials.Keys.Max() + 1)
            .Select(index => materials.TryGetValue(index, out var value) ? value : default)
            .ToArray();
    }
}
