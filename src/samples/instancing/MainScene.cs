using OpenRender.Core;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Samples.Instancing;

internal class MainScene : Scene
{
    private const int NUM_INSTANCES = 15000;
    private const int AREA_HALF_WIDTH = 250;

    private TextRenderer tr = default!;
    private InstancedSceneNode<Matrix4> instanced = default!;
    private bool isMouseMoving;

    public override void Load()
    {
        //  setup background, light and camera
        BackgroundColor = Color4.DarkSlateGray;
        var dirLight = new LightUniform()
        {
            Direction = new Vector3(-0.05f, 0.695f, -0.75f),
            Ambient = new Vector3(0.065f, 0.06f, 0.065f),
            Diffuse = new Vector3(0.8f, 1, 1),
            Specular = new Vector3(1),
        };
        AddLight(dirLight);
        camera = new Camera3D(Vector3.Zero, SceneManager.Size.X / (float)SceneManager.Size.Y, farPlane: 2000);

        //  add text renderer
        var fontAtlas = FontAtlasGenerator.Create("Resources/consola.ttf", 18, new Color4(0f, 0f, 0f, 0.5f));
        tr = new TextRenderer(TextRenderer.CreateTextRenderingProjection(SceneManager.ClientSize.X, SceneManager.ClientSize.Y), fontAtlas);

        base.Load();

        //  add instanced cubes
        var vbBox = GeometryHelper.CreateCube(true);
        var mat = Material.Create(new TextureDescriptor[] {
            new TextureDescriptor("Resources/awesomeface.png", TextureType: TextureType.Diffuse)},
            Vector3.One,
            Vector3.One);
        instanced = new InstancedSceneNode<Matrix4>(new Mesh(vbBox), mat);
        AddNode(instanced);

        for (var i = 0; i < NUM_INSTANCES; i++)
        {
            var position = new Vector3(Random.Shared.Next(-AREA_HALF_WIDTH, AREA_HALF_WIDTH + 1),
                Random.Shared.Next(-AREA_HALF_WIDTH, AREA_HALF_WIDTH + 1),
                Random.Shared.Next(-AREA_HALF_WIDTH, 0));
            var scale = new Vector3(Random.Shared.Next(2, 5));
            var rotation = new Vector3(Random.Shared.Next() * MathF.PI * 2);
            var m = Matrix4.CreateScale(scale) *
                    Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(rotation)) *
                    Matrix4.CreateTranslation(position);
            instanced.AddInstanceData(m);
        }

        //  one non-instanced node for anchoring when moving around
        var mat1 = Material.Create(
            new TextureDescriptor[] {
                new TextureDescriptor ("Resources/container.png", TextureType: TextureType.Detail),
                new TextureDescriptor("Resources/awesomeface.png", TextureType: TextureType.Diffuse)
            },
            detailTextureFactor: 2f,
            shininess: 0.15f
        );
        var visualAnchor = new SceneNode(new Mesh(vbBox), mat1, new Vector3(0, 2, -5));
        AddNode(visualAnchor);
    }

    public override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        tr.Projection = TextRenderer.CreateTextRenderingProjection(SceneManager.ClientSize.X, SceneManager.ClientSize.Y);
    }

    public override void OnMouseWheel(MouseWheelEventArgs e) => camera!.Fov -= e.OffsetY * 5;

    private const int Padding = 51;
    private readonly string helpText1 = $"WASD: move, L shift: down, space: up".PadRight(Padding);
    private readonly string helpText2 = $"mouse: rotate, scroll: zoom, Q: roll L, E: roll R".PadRight(Padding);
    private readonly string helpText3 = $"F11: toggle full screen".PadRight(Padding);
    private readonly Vector3 textColor1 = new(1, 1, 1);
    private readonly Vector3 textColor2 = new(0.8f, 0.8f, 0.65f);

    public override void RenderFrame(double elapsedSeconds)
    {
        base.RenderFrame(elapsedSeconds);
        const int LineHeight = 18;
        const int TextStartY = 10;

        var nodesText = $"nodes: {NUM_INSTANCES}, pos: {camera!.Position:N2}".PadRight(Padding);
        tr.Render(nodesText, 5, TextStartY, textColor1);
        var fpsText = $"avg frame duration: {SceneManager.AvgFrameDuration:G3} ms, fps: {SceneManager.Fps:N0}".PadRight(Padding);
        tr.Render(fpsText, 5, TextStartY + LineHeight * 1, textColor1);
        tr.Render(helpText1, 5, TextStartY + LineHeight * 2, textColor2);
        tr.Render(helpText2, 5, TextStartY + LineHeight * 3, textColor2);
        tr.Render(helpText3, 5, TextStartY + LineHeight * 4, textColor2);

        // currently the text renderer changes GL state directly so the rendering 
        // pipeline must to be notified that material state needs to be re-binding
        ResetMaterial();
    }

    public override void UpdateFrame(double elapsedSeconds)
    {
        if (!SceneManager.IsFocused)
        {
            return;
        }

        base.UpdateFrame(elapsedSeconds);

        HandleRotation(elapsedSeconds);
        HandleMovement(elapsedSeconds);

        if (SceneManager.KeyboardState.IsKeyDown(Keys.Escape))
        {
            SceneManager.Close();
        }

        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F1))
        {
            ShowBoundingSphere = !ShowBoundingSphere;
        }

        if (SceneManager.KeyboardState.IsKeyPressed(Keys.F11))
        {
            SceneManager.WindowState = SceneManager.WindowState == WindowState.Fullscreen ?
                    WindowState.Normal : WindowState.Fullscreen;
        }

        HandleInstanceUpdates(elapsedSeconds);
    }

    private void HandleMovement(double elapsedTime)
    {
        const float MovementSpeed = 5;
        var movementPerSecond = (float)elapsedTime * MovementSpeed;

        var input = SceneManager.KeyboardState;
        if (input.IsKeyDown(Keys.W))
        {
            camera!.Position += camera.Front * movementPerSecond; // Forward
        }

        if (input.IsKeyDown(Keys.S))
        {
            camera!.Position -= camera.Front * movementPerSecond; // Backwards
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

    private void HandleRotation(double elapsedTime)
    {
        const float RotationSpeed = 25;

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

    private int lastInstanceIndex = 0;
    private void HandleInstanceUpdates(double elapsedSeconds)
    {
        //if (instanced == null) return;

        const int BatchCount = 2500;
        var idx = 0;
        var counter = 0;
        while (counter++ < BatchCount)
        {
            idx = (lastInstanceIndex + counter) % NUM_INSTANCES;
            var matrix = instanced.InstanceDataList[idx];
            var position = matrix.Row3.Xyz;
            var scale = new Vector3(
                matrix.Column0.Xyz.Length,
                matrix.Column1.Xyz.Length,
                matrix.Column2.Xyz.Length
            );
            matrix = Matrix4.CreateScale(scale) *
            //Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(rotation)) *
            Matrix4.CreateTranslation(position);
            instanced.InstanceDataList[idx] = matrix;
        }
        lastInstanceIndex = idx;

        //for (var i = 0; i < NUM_INSTANCES; i++)
        //{
        //    var matrix = instanced.InstanceDataList[i];
        //    var position = matrix.Row3.Xyz;
        //    var scale = new Vector3(
        //        matrix.Column0.Xyz.Length,
        //        matrix.Column1.Xyz.Length,
        //        matrix.Column2.Xyz.Length
        //    );
        //    matrix = Matrix4.CreateScale(scale) *
        //    //Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(rotation)) *
        //    Matrix4.CreateTranslation(position);
        //    instanced.InstanceDataList[i] = matrix;
        //}
        instanced.UpdateInstanceData();
    }
}
