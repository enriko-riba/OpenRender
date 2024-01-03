using OpenRender.Components;
using OpenRender.Core;
using OpenRender.Core.Buffers;
using OpenRender.Core.Geometry;
using OpenRender.Core.Rendering;
using OpenRender.Core.Textures;
using OpenRender.SceneManagement;
using OpenRender.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Samples.Instancing;

internal class InstancingScene : Scene
{
    private const int NUM_INSTANCES = 25000;
    private const int AREA_HALF_WIDTH = 250;

    private TextRenderer tr = default!;
    private InstancedSceneNode<Matrix4, InstanceState> instanced = default!;
    private bool isMouseMoving;

    public override void Load()
    {
        base.Load();

        //  setup background, light and camera
        BackgroundColor = Color4.Teal;
        var dirLight = new LightUniform()
        {
            Direction = new Vector3(-0.05f, 0.695f, -0.75f),
            Ambient = new Vector3(0.065f, 0.06f, 0.065f),
            Diffuse = new Vector3(0.8f, 1, 1),
            Specular = new Vector3(1),
        };
        AddLight(dirLight);
        camera = new Camera3D(Vector3.Zero, Width / (float)Height, farPlane: 2000);

        //  add text renderer
        var fontAtlas = FontAtlasGenerator.Create("Resources/consola.ttf", 18, new Color4(0f, 0f, 0f, 0.5f));
        tr = new TextRenderer(TextRenderer.CreateTextRenderingProjection(Width, Height), fontAtlas);

        //  add one non-instanced node for visual anchoring when moving around
        var mat1 = Material.Create(
            defaultShader,
            [
                new TextureDescriptor ("Resources/container.png", TextureType: TextureType.Diffuse),
                new TextureDescriptor ("Resources/container-normal.png", TextureType: TextureType.Normal),
            ],
            diffuseColor: Vector3.One,
            emissiveColor: new Vector3(0.05f, 0.04f, 0.03f),
            specularColor: Vector3.Zero,
            shininess: 0,
            detailTextureScaleFactor: 0
        );
        var (vertices, indices) = GeometryHelper.CreateBox();
        var mesh = new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices);
        var visualAnchor = new SceneNode(mesh, mat1, new Vector3(0, 2, -5));
        AddNode(visualAnchor);

        AddInstancedNodes();
    }

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

        // currently the text renderer changes GL state directly so the rendering pipeline must 
        // be notified that material state needs to be re-bound before rendering the next node
        renderer.ResetMaterial();
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

    public override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        tr.Projection = TextRenderer.CreateTextRenderingProjection(Width, Height);
    }

    public override void OnMouseWheel(MouseWheelEventArgs e) => camera!.Fov -= e.OffsetY * 5;

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

    /// <summary>
    /// Updates every instances state and instance data.
    /// Note: this is not performant as 25000 matrix calculations and state updates
    /// per frame heavily outweigh the render time, but it's just a sample.
    /// </summary>
    /// <param name="elapsedSeconds"></param>
    private void HandleInstanceUpdates(double elapsedSeconds)
    {
        for (var i = 0; i < NUM_INSTANCES; i++)
        {
            instanced.StateDataList[i].Update(elapsedSeconds);
            instanced.StateDataList[i].GetMatrix(out var matrix);
            instanced.InstanceData[i] = matrix;
        }
        instanced.UpdateInstanceData();
    }

    /// <summary>
    /// Adds a bunch of instanced nodes to the scene.
    /// </summary>
    private void AddInstancedNodes()
    {
        //  add instanced cubes
        var (vertices, indices) = GeometryHelper.CreateBox();
        var mat = Material.Create(
            defaultShader,
            [new("Resources/awesomeface.png", TextureType: TextureType.Diffuse)],
            diffuseColor: Vector3.One,
            specularColor: Vector3.One,
            shininess: 0.45f);
        instanced = new InstancedSceneNode<Matrix4, InstanceState>(new Mesh(VertexDeclarations.VertexPositionNormalTexture, vertices, indices), NUM_INSTANCES, mat);
        AddNode(instanced);

        for (var i = 0; i < NUM_INSTANCES; i++)
        {
            var position = new Vector3(Random.Shared.Next(-AREA_HALF_WIDTH, AREA_HALF_WIDTH + 1),
                Random.Shared.Next(-AREA_HALF_WIDTH, AREA_HALF_WIDTH + 1),
                Random.Shared.Next(-AREA_HALF_WIDTH, AREA_HALF_WIDTH));
            var axis = Random.Shared.Next(0, 3);
            var state = new InstanceState()
            {
                Position = position,
                Scale = new Vector3(Random.Shared.Next(1, 6)),
                AxisOfRotation = new Vector3(
                    axis == 0 ? 1 : 0,
                    axis == 1 ? 1 : 0,
                    axis == 2 ? 1 : 0),
            };
            state.GetMatrix(out var m);
            instanced.AddInstanceData(m, state);
        }
    }

    /// <summary>
    /// Class holding the state of a single instance.
    /// </summary>
    private sealed class InstanceState
    {
        private Matrix4 scaleMatrix;
        private Vector3 scale = Vector3.One;
        private readonly float factor = 1 + (float)Random.Shared.NextDouble() / 2;

        /// <summary>
        /// Gets or sets the position.
        /// </summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// Gets or sets the scale.
        /// </summary>
        public Vector3 Scale
        {
            get => scale;
            set
            {
                scale = value;
                scaleMatrix = Matrix4.CreateScale(Scale);
            }
        }

        /// <summary>
        /// Axis rotation value in radians.
        /// </summary>
        public Vector3 Rotation { get; set; } = Vector3.Zero;

        /// <summary>
        /// Vector with one component set to 1 marking the axis of rotation.
        /// </summary>
        public Vector3 AxisOfRotation { get; set; }

        /// <summary>
        /// Updates the rotation.
        /// </summary>
        /// <param name="elapsedSeconds"></param>
        public void Update(double elapsedSeconds) => Rotation += AxisOfRotation * (float)elapsedSeconds * factor;

        /// <summary>
        /// Calculates the world matrix for this instance.
        /// </summary>
        /// <param name="matrix"></param>
        public void GetMatrix(out Matrix4 matrix)
        {
            Matrix4.CreateFromQuaternion(new Quaternion(Rotation), out var rotationMatrix);
            Matrix4.Mult(scaleMatrix, rotationMatrix, out matrix);
            matrix.Row3.Xyz = Position;    //  sets the translation
        }
    }
}
