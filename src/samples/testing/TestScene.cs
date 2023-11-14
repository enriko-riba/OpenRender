using OpenRender.Components;
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
        if (SceneManager.KeyboardState.IsKeyDown(Keys.Escape))  SceneManager.Close();
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
            new TextureDescriptor[] { new TextureDescriptor("Resources/container.png", TextureType: TextureType.Diffuse),
            new TextureDescriptor ("Resources/container-normal.png", TextureType: TextureType.Normal)},
            0.70f);
        mat.EmissiveColor = new(0.05f, 0.09f, 0.05f);
        for (var i = 0; i < 50; i++)
        {
            var box = new SceneNode(new Mesh(Vertex.VertexDeclaration, vertices, indices), mat);
            box.SetPosition(new(-250 + i * 10, 0, -50));
            AddNode(box);
            box.IsBatchingAllowed = true;
        }
    }
}
