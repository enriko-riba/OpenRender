using ImGuiNET;
using OpenRender.Core;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Rendering.Text;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace testing;

internal class TestScene : Scene
{
    private readonly ITextRenderer tr;


    public TestScene(ITextRenderer textRenderer) : base()
    {
        BackgroundColor = Color4.DarkBlue;
        tr = textRenderer;
    }

    private readonly Vector3 textColor1 = new(1, 1, 0.3f);

    public override void Load()
    {
        base.Load();

        var context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();


        var dirLight = new LightUniform()
        {
            Direction = new Vector3(-0.95f, -0.995f, 0.85f),
            Ambient = new Vector3(0.1f, 0.05f, 0.1f),
            Diffuse = new Vector3(0.8f),
            Specular = new Vector3(1),
        };
        AddLight(dirLight);
        camera = new Camera3D(Vector3.Zero, SceneManager.Size.X / (float)SceneManager.Size.Y, farPlane: 2000);
        AddMetallicBoxes();
    }

    public override void OnMouseWheel(MouseWheelEventArgs e)
    {
        const float Sensitivity = 3.5f;
        camera!.Fov -= e.OffsetY * Sensitivity;
    }

    public override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        tr.Projection = TextRenderer.CreateTextRenderingProjection(Width, Height);
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        base.UpdateFrame(elapsedSeconds);

        if (!SceneManager.IsFocused) return;
        if (SceneManager.KeyboardState.IsKeyDown(Keys.Escape)) SceneManager.Close();
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F1)) ShowBoundingSphere = !ShowBoundingSphere;
        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F11)) SceneManager.WindowState = SceneManager.WindowState == WindowState.Fullscreen ?
            WindowState.Normal : WindowState.Fullscreen;

        HandleMovement(elapsedSeconds);
        HandleRotation(elapsedSeconds);
    }

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        const int LineHeight = 18;
        const int TextStartY = 0;
        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}";
        tr.Render(fpsText, 5, TextStartY + LineHeight * 1, textColor1);

        //ImGuiTest(elapsedSeconds);
    }

    private void HandleMovement(double elapsedTime)
    {
        const float MovementSpeed = 100;
        var movementPerSecond = (float)elapsedTime * MovementSpeed;

        var input = SceneManager.KeyboardState;
        if (input.IsKeyDown(Keys.W))
        {
            camera!.MoveForward(movementPerSecond);
        }
        if (input.IsKeyDown(Keys.S))
        {
            camera!.MoveForward(-movementPerSecond);
        }
        if (input.IsKeyDown(Keys.A))
        {
            camera!.Position -= camera.Right * movementPerSecond; // Left
        }
        if (input.IsKeyDown(Keys.D))
        {
            camera!.Position += camera.Right * movementPerSecond; // Right
        }
        if (input.IsKeyDown(Keys.Space))
        {
            camera!.Position += camera.Up * movementPerSecond; // Up
        }
        if (input.IsKeyDown(Keys.LeftShift))
        {
            camera!.Position -= camera.Up * movementPerSecond; // Down
        }
    }

    private bool isMouseMoving;
    private void HandleRotation(double elapsedTime)
    {
        const float RotationSpeed = 100;

        var mouseState = SceneManager.MouseState;
        var rotationPerSecond = (float)(elapsedTime * RotationSpeed);

        if (SceneManager.KeyboardState.IsKeyDown(Keys.Q))
        {
            camera!.AddRotation(0, 0, rotationPerSecond);
        }
        if (SceneManager.KeyboardState.IsKeyDown(Keys.E))
        {
            camera!.AddRotation(0, 0, -rotationPerSecond);
        }
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

    private void AddMetallicBoxes()
    {
        var (vertices, indices) = GeometryHelper.CreateBox();
        var shader = new Shader("Shaders/standard-batching.vert", "Shaders/standard-batching.frag");
        var mat = Material.Create(shader,
            [
                new("Resources/container.png", TextureType: TextureType.Diffuse),
                new("Resources/container-normal.png", TextureType: TextureType.Normal)
            ],
            0.70f);
        mat.EmissiveColor = new(0.05f, 0.09f, 0.05f);
        for (var i = 0; i < 50; i++)
        {
            var box = new TestNode(new Mesh(Vertex.VertexDeclaration, vertices, indices), mat);
            box.SetPosition(new(-250 + i * 10, 0, -50));
            AddNode(box);
            box.IsBatchingAllowed = true;
        }
    }


    private float _f;
    private bool _showImGuiDemoWindow;
    private int _counter;
    private int _dragInt;

    private void ImGuiTest(double elapsedSeconds)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(SceneManager.Size.X, SceneManager.Size.Y);
        io.DeltaTime = (float)elapsedSeconds; // DeltaTime is in seconds.
        ImGui.NewFrame();
        ImGui.Text("");
        ImGui.Text(string.Empty);
        ImGui.Text("Hello, world!");                                        // Display some text (you can use a format string too)
        ImGui.SliderFloat("float", ref _f, 0, 1, _f.ToString("0.000"));  // Edit 1 float using a slider from 0.0f to 1.0f    
                                                                         //ImGui.ColorEdit3("clear color", ref _clearColor);                   // Edit 3 floats representing a color

        ImGui.Text($"Mouse position: {ImGui.GetMousePos()}");

        ImGui.Checkbox("ImGui Demo Window", ref _showImGuiDemoWindow);                 // Edit bools storing our windows open/close state
        //ImGui.Checkbox("Another Window", ref _showAnotherWindow);
        //ImGui.Checkbox("Memory Editor", ref _showMemoryEditor);
        if (ImGui.Button("Button"))                                         // Buttons return true when clicked (NB: most widgets return true when edited/activated)
            _counter++;
        ImGui.SameLine(0, -1);
        ImGui.Text($"counter = {_counter}");

        ImGui.DragInt("Draggable Int", ref _dragInt);

        var framerate = ImGui.GetIO().Framerate;
        ImGui.Text($"Application average {1000.0f / framerate:0.##} ms/frame ({framerate:0.#} FPS)");
    }
}

internal class TestNode : SceneNode
{
    private readonly int rotationAxis;

    public TestNode(Mesh mesh, Material material) : base(mesh, material)
    {
        IsBatchingAllowed = true;
        rotationAxis = Random.Shared.Next(0, 2);
    }

    public override void OnUpdate(Scene scene, double elapsedSeconds)
    {
        var rot = AngleRotation;
        if (rotationAxis == 0) rot.X += (float)(Random.Shared.NextDouble() * elapsedSeconds * 2);
        if (rotationAxis == 1) rot.Y += (float)(Random.Shared.NextDouble() * elapsedSeconds * 2);
        if (rotationAxis == 2) rot.Z += (float)(Random.Shared.NextDouble() * elapsedSeconds * 2);
        SetRotation(rot);
        base.OnUpdate(scene, elapsedSeconds);
    }
}