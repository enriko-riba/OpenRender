global using AABB = (OpenTK.Mathematics.Vector3i min, OpenTK.Mathematics.Vector3i max);
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
    private const float RotationSpeed = 20;
    private float movementPerSecond = MovementSpeed;
    private float rotationPerSecond = RotationSpeed;

    private Vector2 mouseCenter;
    private Vector2 lastMousePosition;

    private Chunk? cameraCurrentChunk;
    private Vector3i cameraCurrentChunkLocalPosition;
    private LightUniform dirLight;
    private VoxelWorld world = default!;

    private bool dbgIsFlyMode;

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
        //var startPosition = new Vector3(10, 10, 10);
        camera = new CameraFps(startPosition, Width / (float)Height, 0.1f, VoxelHelper.FarPlane * 10) // TODO: x 10 is for debugging loading/unloading chunks
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
            new KeyboardAction("fly mode", [Keys.F], ()=> dbgIsFlyMode = !dbgIsFlyMode),
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
            textRenderer.Render(text.PadRight(Padding, ' '), 20, 5, lineY, color);
            lineY += 20;
        }
        writeLine("", textColor);

        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}";
        writeLine(fpsText, textColor);

        var text = $"World: size {VoxelHelper.WorldChunksXZ:N0}, chunks {VoxelHelper.TotalChunks:N0}, chunk size: {VoxelHelper.ChunkSideSize}, max chunk dist.: {VoxelHelper.MaxDistanceInChunks}";
        writeLine(text, textColor);

        var surroundingChunks = world.SurroundingChunkIndices.Count;
        text = $"Chunks: loaded {world.LoadedChunks.Count}, surrounding {surroundingChunks}, in frustum {world.ChunkRenderer.ChunksInFrustum:N0}/{surroundingChunks - world.ChunkRenderer.ChunksInFrustum:N0}";
        writeLine(text, textColor);

        text = $"Blocks rendered {world.ChunkRenderer.RenderedBlocks:N0}, worker queue {world.WorkerQueueLength}, render data {world.ChunkRenderer.ChunkRenderDataLength}";
        writeLine(text, textColor);

        text = $"position: {camera?.Position}";
        writeLine(text, debugColorBluish);

        //  show chunk index and position
        if (cameraCurrentChunk is not null)
        {
            text = $"in chunk: {cameraCurrentChunk} at {cameraCurrentChunk.GetTopBlockAtLocalXZ(cameraCurrentChunkLocalPosition)}";
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
            cameraCurrentChunkLocalPosition = VoxelHelper.GetBlockCoordinatesGlobal(pos) - chunk!.Position;
            if (!dbgIsFlyMode)
            {
                var height = world.GetHeightNormalizedGlobal((int)MathF.Floor(pos.X), (int)MathF.Floor(pos.Z));
                pos.Y = MathF.Floor(height) + 3f;
                camera.Position = pos;
            }
        }
        else
        {
            cameraCurrentChunk = null;
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
            camera!.AddRotation(delta.X * rotationPerSecond, delta.Y * rotationPerSecond, 0);

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
