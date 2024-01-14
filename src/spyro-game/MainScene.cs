global using AABB = (OpenTK.Mathematics.Vector3i Min, OpenTK.Mathematics.Vector3i Max);
using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Buffers;
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
using SpyroGame.Input;
using SpyroGame.World;

namespace SpyroGame;

internal class MainScene(ITextRenderer textRenderer) : Scene
{
    private const int Padding = 70;

    private static readonly Vector3 textColor = new(0.752f, 0.750f, 0);
    private static readonly Vector3 debugColorBluish = new(0.2f, 0.2f, 1f);

    private readonly KeyboardActionMapper kbdActions = new();
    private DayNightCycle dayNightCycle = default!;
    private Sprite crosshair = default!;

    private const float MovementSpeed = 4;
    private const float RotationSpeed = 10;
    private float movementPerSecond = MovementSpeed;
    private float rotationPerSecond = RotationSpeed;

    private Vector2 mouseCenter;
    private Vector2 lastMousePosition;

    private LightUniform dirLight;
    private VoxelWorld world = default!;
    private Player player = default!;

    public VoxelWorld World
    {
        set => world = value;
    }

    public override void Load()
    {
        base.Load();
        BackgroundColor = Color4.DarkSlateBlue;

        SceneManager.CursorState = CursorState.Hidden;
        mouseCenter = new Vector2(Width, Height) / 2;
        SceneManager.MousePosition = mouseCenter;

        var startPosition = new Vector3(VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f, 10, VoxelHelper.ChunkSideSize * VoxelHelper.WorldChunksXZ / 2f);
        //var startPosition = new Vector3(0, -1, 0);
        camera = new CameraFps(startPosition, Width / (float)Height, 0.1f, VoxelHelper.FarPlane * 10) // TODO: x 10 is for debugging loading/unloading chunks
        {
            MaxFov = 40
        };

        player = new Player(camera, startPosition);

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
            new KeyboardAction("fly mode", [Keys.F], ()=> player.IsGhostMode = !player.IsGhostMode),
            new KeyboardAction("forward", [Keys.W], ()=> player.MoveForward(movementPerSecond), false),
            new KeyboardAction("left", [Keys.A], ()=> player.MoveLeft(movementPerSecond), false),
            new KeyboardAction("right", [Keys.D], ()=> player.MoveRight(movementPerSecond), false),
            new KeyboardAction("back", [Keys.S], ()=> player.MoveForward(-movementPerSecond), false),
            new KeyboardAction("rot CCW", [Keys.Q], ()=> player.AddRotation(0, 0, -rotationPerSecond), false),
            new KeyboardAction("rot CW", [Keys.E], ()=> player.AddRotation(0, 0, rotationPerSecond), false),
            new KeyboardAction("up", [Keys.LeftShift], ()=> player.Position += Vector3.UnitY * movementPerSecond, false),
            new KeyboardAction("down", [Keys.LeftControl], ()=> player.Position -= Vector3.UnitY * movementPerSecond, false),
            new KeyboardAction("wireframe", [Keys.F1], WireframeToggle),
            new KeyboardAction("pick block", [Keys.P], CalcBlockPicking),
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

    private PickedBlock? pickedBlock = null;

    private void CalcBlockPicking()
    {
        pickedBlock = world.PickBlock();
        world.ChunkRenderer.PickedBlock = pickedBlock;
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);

        var lineY = 4;
        void writeLine(string text, in Vector3 color)
        {
            textRenderer.Render(text.PadRight(Padding, ' '), 20, 5, lineY, color);
            lineY += 20;
        }
        writeLine("", textColor);

        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}";
        writeLine(fpsText, textColor);

        var text = $"World: size {VoxelHelper.WorldChunksXZ:N0}, chunk size: {VoxelHelper.ChunkSideSize}, max chunk dist.: {VoxelHelper.MaxDistanceInChunks}";
        writeLine(text, textColor);

        var surroundingChunks = world.SurroundingChunkIndices.Count;
        text = $"Chunks: chunks {VoxelHelper.TotalChunks:N0}, surrounding {surroundingChunks}, loaded {world.LoadedChunks}, cached {world.CachedChunks}";
        writeLine(text, textColor);

        text = $"in frustum {world.ChunkRenderer.ChunksInFrustum:N0}/{surroundingChunks - world.ChunkRenderer.ChunksInFrustum:N0}";
        writeLine(text, textColor);

        text = $"Blocks rendered {world.ChunkRenderer.RenderedBlocks:N0}, worker queue {world.WorkerQueueLength}, render data {world.ChunkRenderer.ChunkRenderDataLength}";
        writeLine(text, textColor);

        text = $"position {camera?.Position.ToString("N2")}, picked block {pickedBlock?.blockIndex}{(player.IsGhostMode?", ghost mode":"")}";
        writeLine(text, debugColorBluish);

        //  show chunk index and position
        if (player.CurrentChunk is not null)
        {
            text = $"in chunk: {player.CurrentChunk} at {player.CurrentChunk.GetTopBlockAtLocalXZ(player.ChunkLocalPosition)}";
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

        var pos = player.Position;
        if (world.GetChunkByGlobalPosition(pos, out var chunk))
        {            
            player.CurrentChunk = chunk;
            player.ChunkLocalPosition = (Vector3i)pos - chunk!.Position;
            if (!player.IsGhostMode)
            {
                var height = chunk!.GetTerrainHeightAt((int)player.ChunkLocalPosition.X, (int)player.ChunkLocalPosition.Z);
                pos.Y = height;
                player.Position = pos;
            }
        }
        else
        {
            player.CurrentChunk = null;
        }

        rotationPerSecond = (float)(elapsedSeconds * RotationSpeed);
        movementPerSecond = (float)elapsedSeconds * MovementSpeed;
        kbdActions.Update(SceneManager.KeyboardState);

        //  mouse movement
        var mousePos = SceneManager.MouseState.Position;
        var delta = lastMousePosition - mousePos;
        lastMousePosition = mousePos;
        if (delta.LengthSquared > 0)
        {
            //camera!.AddRotation(delta.X * rotationPerSecond, delta.Y * rotationPerSecond, 0);
            player.AddRotation(delta.X * rotationPerSecond, delta.Y * rotationPerSecond, 0);

            if (mousePos.X < 100 ||
                mousePos.Y < 100 ||
                mousePos.X > Width - 100 ||
                mousePos.Y > Height - 100)
            {
                SceneManager.MousePosition = mouseCenter;
                lastMousePosition = mouseCenter;
            }
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

        mouseCenter = clientSize / 2;
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
        
        var (vertices, indices) = VoxelHelper.CreateVoxelCube();
        var cubeMaterial = Material.Create(defaultShader,
            [new("Resources/voxel/box-unwrap.png", TextureType: TextureType.Diffuse)],
            //[new("Resources/Corey.png", TextureType: TextureType.Diffuse)],
            0.10f);
        cubeMaterial.EmissiveColor = new(0.05f, 0.07f, 0.005f);
        var cube = new SceneNode(new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices), cubeMaterial);
        cube.SetPosition(new(0, 0, 0));
        AddNode(cube);

        AddNode(world.ChunkRenderer);
        world.Camera = camera!;
        camera!.Invalidate();

        dayNightCycle.Update();
    }
}
